using System;
using System.Collections.Generic;
using CoreAI.Logging;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using Newtonsoft.Json.Linq;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Ai
{
    /// <summary>
    /// Orchestration pipeline: prompts, memory, <see cref="ILlmClient"/> invocation,
    /// optional structured-output retry via <see cref="IRoleStructuredResponsePolicy"/>, command publication.
    /// </summary>
    public sealed class AiOrchestrator : IAiOrchestrationService
    {
        /// <summary>Used when <see cref="ICoreAISettings.EnableConversationHistorySummarization"/> is false so the full transcript stays in the MEAI tail.</summary>
        internal const int UnlimitedHistoryTokenBudget = 2_000_000;

        private readonly IAuthorityHost _authority;
        private readonly ILlmClient _llm;
        private readonly IAiGameCommandSink _commandSink;
        private readonly ISessionTelemetryProvider _telemetry;
        private readonly AiPromptComposer _promptComposer;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly AgentMemoryPolicy _memoryPolicy;
        private readonly IRoleStructuredResponsePolicy _structuredPolicy;
        private readonly IAiOrchestrationMetrics _metrics;
        private readonly ICoreAISettings _settings;
        private readonly IConversationContextManager _contextManager;
        private readonly IAgentTurnTraceSink _traceSink;
        private readonly IContextBudgetPolicy _contextBudgetPolicy;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IConversationCompactionCoordinator _compactionCoordinator;

        /// <summary>Constructs orchestrator dependencies (usual registration path: DI container).</summary>
        public AiOrchestrator(
            IAuthorityHost authority,
            ILlmClient llm,
            IAiGameCommandSink commandSink,
            ISessionTelemetryProvider telemetry,
            AiPromptComposer promptComposer,
            IAgentMemoryStore memoryStore,
            AgentMemoryPolicy memoryPolicy,
            IRoleStructuredResponsePolicy structuredPolicy,
            IAiOrchestrationMetrics metrics,
            ICoreAISettings settings,
            IConversationContextManager contextManager = null,
            IAgentTurnTraceSink traceSink = null,
            IContextBudgetPolicy contextBudgetPolicy = null,
            ITokenEstimator tokenEstimator = null,
            IConversationCompactionCoordinator compactionCoordinator = null)
        {
            _authority = authority;
            _llm = llm;
            _commandSink = commandSink;
            _telemetry = telemetry;
            _promptComposer = promptComposer;
            _memoryStore = memoryStore;
            _memoryPolicy = memoryPolicy;
            _structuredPolicy = structuredPolicy ?? new NoOpRoleStructuredResponsePolicy();
            _metrics = metrics ?? new NullAiOrchestrationMetrics();
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _contextManager = contextManager ??
                              new DeterministicConversationContextManager(new InMemoryConversationSummaryStore());
            _traceSink = traceSink ?? new NullAgentTurnTraceSink();
            _contextBudgetPolicy = contextBudgetPolicy ?? new DefaultContextBudgetPolicy();
            _tokenEstimator = tokenEstimator ?? new HeuristicTokenEstimator();
            _compactionCoordinator = compactionCoordinator ?? new DefaultConversationCompactionCoordinator();
        }

        /// <summary>
        /// Builds the request bundle shared by <see cref="RunTaskAsync"/> and <see cref="RunStreamingAsync"/>.
        /// </summary>
        private async Task<RequestBundle> BuildRequestAsync(
            AiTaskRequest task,
            int contextRetryPass,
            CancellationToken cancellationToken)
        {
            string roleId = string.IsNullOrWhiteSpace(task.RoleId) ? BuiltInAgentRoleIds.Creator : task.RoleId.Trim();
            string traceId = string.IsNullOrWhiteSpace(task.TraceId)
                ? Guid.NewGuid().ToString("N")
                : task.TraceId.Trim();
            GameSessionSnapshot snap = _telemetry.BuildSnapshot();
            string systemBase = _promptComposer.GetSystemPrompt(roleId);
            string worldState = _promptComposer.BuildRuntimeContext(task, roleId, traceId);

            string system = systemBase;
            AgentMemoryState memoryState = null;
            AgentMemoryPromptParts memoryParts = default;
            bool useMemoryTool = _memoryPolicy?.IsMemoryEnabled(roleId) ?? false;
            if (useMemoryTool &&
                _memoryStore != null && _memoryStore.TryLoad(roleId, out memoryState) &&
                !string.IsNullOrWhiteSpace(memoryState?.Memory))
            {
                if (AgentMemoryPromptPlacement.NeedsInitialSnapshot(memoryState) &&
                    AgentMemoryPromptPlacement.ConsolidateSnapshot(memoryState))
                {
                    _memoryStore.Save(roleId, memoryState);
                }

                memoryParts = AgentMemoryPromptPlacement.Build(memoryState);
                system = AppendTailBudgetSection(systemBase, memoryParts.PrefixBlock);
            }

            string user = _promptComposer.BuildUserPayload(snap, task);
            IReadOnlyList<ILlmTool> tools = _memoryPolicy?.GetToolsForRole(roleId);
            tools = FilterToolsForRequest(tools, task);
            system = AppendToolContract(system, tools, task, roleId);
            string systemForBudget = AppendTailBudgetSection(system, memoryParts.TailBlock);

            AgentMemoryPolicy.RoleMemoryConfig roleConfig =
                _memoryPolicy?.GetRoleConfig(roleId) ?? new AgentMemoryPolicy.RoleMemoryConfig();
            roleConfig = ResolveRoleConfigForRequest(roleConfig, task);

            int contextWindowTokens = roleConfig.ContextTokens > 0
                ? roleConfig.ContextTokens
                : _settings.ContextWindowTokens;

            ConversationContextBuildArgs ctxBuildArgs = null;
            int? resolvedMaxOutput = ResolveMaxOutputTokens(task.MaxOutputTokens, roleConfig.MaxOutputTokens);
            ContextBudget budget = default;
            if (roleConfig.WithChatHistory && _memoryStore != null)
            {
                ContextBudgetRequest budgetRequest = new()
                {
                    MaxContextTokens = contextWindowTokens,
                    SystemPrompt = systemForBudget,
                    UserPayload = user,
                    Tools = tools,
                    MaxOutputTokens = resolvedMaxOutput,
                    ContextRetryLevel = contextRetryPass
                };
                budget = _contextBudgetPolicy.Compute(budgetRequest, _tokenEstimator);
                int historyBudget = budget.HistoryTokenBudget;
                if (!_settings.EnableConversationHistorySummarization)
                {
                    historyBudget = UnlimitedHistoryTokenBudget;
                }
                else if (_settings.ConversationHistoryRecentTokenBudgetOverride > 0)
                {
                    historyBudget = Math.Max(32, _settings.ConversationHistoryRecentTokenBudgetOverride);
                }

                // On a context-overflow retry pass the policy budget is progressively shrunk
                // (0.75^retryLevel). The unlimited and fixed-override branches above ignore that shrink,
                // so without this clamp a retry would rebuild a byte-identical oversized request that
                // overflows again (up to MaxContextOverflowRetries wasted calls). Bounding by the shrunk
                // policy budget makes each retry actually reduce the prompt.
                if (contextRetryPass > 0)
                {
                    historyBudget = Math.Min(historyBudget, budget.HistoryTokenBudget);
                }

                int maxRolled = _settings.ConversationRolledSummaryMaxTokens;
                float compactionTriggerRatio =
                    roleConfig.CompactionTriggerRatio ?? _settings.ConversationCompactionTriggerRatio;
                ctxBuildArgs = new ConversationContextBuildArgs
                {
                    HistoryTokenBudget = historyBudget,
                    SourceBudget = budget,
                    UseLlmContextCompaction =
                        _settings.EnableLlmContextCompaction && roleConfig.UseLlmContextCompaction,
                    MaxRolledSummaryTokens = maxRolled > 0 ? maxRolled : 0,
                    CompactionTriggerRatio = compactionTriggerRatio,
                    EnableContextPruning = _settings.EnableContextPruning,
                    MaxRetainedToolResultMessages = _settings.MaxRetainedToolResultMessages
                };
            }

            (string updatedSystem, List<Microsoft.Extensions.AI.ChatMessage> chatHistory, bool wasCompacted) =
                await BuildChatHistoryAsync(roleId, roleConfig, system, ctxBuildArgs, traceId, cancellationToken)
                    .ConfigureAwait(false);
            system = updatedSystem;
            bool shouldConsolidateMemorySnapshot = wasCompacted || contextRetryPass > 0;
            if (shouldConsolidateMemorySnapshot &&
                memoryState != null &&
                AgentMemoryPromptPlacement.HasPendingUpdates(memoryState) &&
                AgentMemoryPromptPlacement.ConsolidateSnapshot(memoryState))
            {
                _memoryStore.Save(roleId, memoryState);
                memoryParts = AgentMemoryPromptPlacement.Build(memoryState);
                system = AppendTailBudgetSection(systemBase, memoryParts.PrefixBlock);
                system = AppendToolContract(system, tools, task, roleId);
            }

            AppendMemoryTailMessage(ref chatHistory, memoryParts.TailBlock);
            AppendWorldStateTailMessage(ref chatHistory, worldState);
            int estimatedPromptTokens =
                _tokenEstimator.EstimateText(system ?? "") +
                _tokenEstimator.EstimateText(user ?? "") +
                EstimateToolsTokens(tools) +
                EstimateChatHistoryTokens(chatHistory);

            return new RequestBundle
            {
                RoleId = roleId,
                TraceId = traceId,
                Snapshot = snap,
                SystemPrompt = system,
                UserPayload = user,
                Tools = tools,
                ChatHistory = chatHistory,
                RoleConfig = roleConfig,
                Task = task,
                ContextWindowTokens = contextWindowTokens,
                HistoryTokenBudget = ctxBuildArgs?.HistoryTokenBudget ?? 0,
                ChatHistoryMessageCount = chatHistory?.Count ?? 0,
                EstimatedPromptTokens = estimatedPromptTokens
            };
        }

        /// <inheritdoc />
        public async Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
            if (!_authority.CanRunAiTasks)
            {
                string denied = UserFacingChatFailureOrNull(task, "AI execution disabled.");
                if (denied != null)
                {
                    return denied;
                }

                return null;
            }

            RequestBundle bundle = null;
            string roleId;
            string traceId;
            string system;
            string user;
            IReadOnlyList<ILlmTool> tools;
            AgentMemoryPolicy.RoleMemoryConfig roleConfig;
            List<Microsoft.Extensions.AI.ChatMessage> chatHistory;
            int? maxOutputTokens;
            LlmCompletionResult result = null;
            int contextPass = 0;
            int contextOverflowPasses = 0;
            int maxContextOverflowRetries = Math.Max(0, _settings.MaxContextOverflowRetries);

            // Single invocation for non-context failures; bounded tighter-history rebuilds when the
            // provider reports context-length overflow. Network retries remain in LoggingLlmClientDecorator.
            try
            {
                while (true)
                {
#if UNITY_WEBGL && !UNITY_EDITOR
                    bundle = await BuildRequestAsync(task, contextPass, cancellationToken);
#else
                    bundle = await BuildRequestAsync(task, contextPass, cancellationToken).ConfigureAwait(false);
#endif
                    roleId = bundle.RoleId;
                    traceId = bundle.TraceId;
                    system = bundle.SystemPrompt;
                    user = bundle.UserPayload;
                    tools = bundle.Tools;
                    roleConfig = bundle.RoleConfig;
                    chatHistory = bundle.ChatHistory;
                    maxOutputTokens = ResolveMaxOutputTokens(task.MaxOutputTokens, roleConfig.MaxOutputTokens);

                    Stopwatch sw = Stopwatch.StartNew();
                    try
                    {
                        // WebGL player: do not detach from the captured Unity SynchronizationContext.
                        // Single-threaded IL2CPP has no real thread pool, so ConfigureAwait(false) here
                        // chat UI stays on typing dots even though the LLM result has arrived.
#if UNITY_WEBGL && !UNITY_EDITOR
                        result = await _llm
                            .CompleteAsync(
                                BuildCompletionRequest(bundle, task, user, maxOutputTokens),
                                cancellationToken);
#else
                        result = await _llm
                            .CompleteAsync(
                                BuildCompletionRequest(bundle, task, user, maxOutputTokens),
                                cancellationToken)
                            .ConfigureAwait(false);
#endif
                    }
                    finally
                    {
                        sw.Stop();
                        _metrics.RecordLlmCompletion(roleId, traceId, result != null && result.Ok,
                            sw.Elapsed.TotalMilliseconds);
                    }

                    if (result != null && result.Ok)
                    {
                        string toolOnlyContent =
                            ResolveToolOnlyCompletionContent(result.Content, result.ExecutedToolCalls);
                        if (!string.IsNullOrEmpty(toolOnlyContent))
                        {
                            result.Content = toolOnlyContent;
                            break;
                        }
                    }

                    bool isContextOverflow = result != null &&
                                             result.ErrorCode == LlmErrorCode.ContextLengthExceeded;
                    bool canRetryContextOverflow = isContextOverflow &&
                                                   _compactionCoordinator.ShouldRetryAfterContextOverflow(
                                                       result,
                                                       contextOverflowPasses,
                                                       maxContextOverflowRetries);
                    if (canRetryContextOverflow)
                    {
                        contextOverflowPasses++;
                        contextPass = contextOverflowPasses;
                        continue;
                    }

                    if (contextOverflowPasses > 0 && isContextOverflow)
                    {
                        RecordTrace(bundle, result, null, result?.Error ?? "empty response");
                        string compactionFail = UserFacingChatFailureOrNull(task, result?.Error ?? "empty response");
                        if (compactionFail != null)
                        {
                            return compactionFail;
                        }

                        return null;
                    }

                    RecordTrace(bundle, result, null, result?.Error ?? "empty response");
                    string emptyFail = UserFacingChatFailureOrNull(task, result?.Error ?? "empty response");
                    if (emptyFail != null)
                    {
                        return emptyFail;
                    }

                    return null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // honour caller cancellation
            }
            catch (Exception ex)
            {
                Log.Instance.Error($"[AiOrchestrator] Task execution failed: {ex.Message}", LogTag.Llm);
                RecordTrace(bundle, result, null, ex.Message);
                string thrown = UserFacingChatFailureOrNull(task, ex.Message);
                if (thrown != null)
                {
                    return thrown;
                }

                return null;
            }

            string content = result.Content;
            if (_structuredPolicy.ShouldValidate(roleId) &&
                !_structuredPolicy.TryValidate(roleId, content, out string failReason))
            {
                _metrics.RecordStructuredRetry(roleId, traceId, failReason ?? "");
                AiTaskRequest retryTask = CloneTaskWithStructuredHint(task, failReason);
                string userRetry = _promptComposer.BuildUserPayload(bundle.Snapshot, retryTask);
                Stopwatch sw = Stopwatch.StartNew();
#if UNITY_WEBGL && !UNITY_EDITOR
                LlmCompletionResult second = await _llm.CompleteAsync(
                    BuildCompletionRequest(bundle, task, userRetry, maxOutputTokens),
                    cancellationToken);
#else
                LlmCompletionResult second = await _llm.CompleteAsync(
                    BuildCompletionRequest(bundle, task, userRetry, maxOutputTokens),
                    cancellationToken).ConfigureAwait(false);
#endif
                sw.Stop();
                _metrics.RecordLlmCompletion(roleId, traceId, second != null && second.Ok,
                    sw.Elapsed.TotalMilliseconds);

                if (second == null || !second.Ok || string.IsNullOrEmpty(second.Content))
                {
                    RecordTrace(bundle, second, null, second?.Error ?? "structured retry failed");
                    string retryFail =
                        UserFacingChatFailureOrNull(task, second?.Error ?? "structured retry failed");
                    if (retryFail != null)
                    {
                        return retryFail;
                    }

                    return null;
                }

                content = second.Content;
                if (!_structuredPolicy.TryValidate(roleId, content, out _))
                {
                    RecordTrace(bundle, second, content, "structured validation failed");
                    RecordTokenObservation(bundle, second);
                    string validFail =
                        UserFacingChatFailureOrNull(task, "Structured response validation failed.");
                    if (validFail != null)
                    {
                        return validFail;
                    }

                    return null;
                }

                result = second;
            }

            content = SanitizeAndPublish(bundle, task, content, user, result);
            return content;
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
            AiTaskRequest task,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            if (!_authority.CanRunAiTasks)
            {
                yield return new LlmStreamChunk { IsDone = true, Error = "authority denied" };
                yield break;
            }

            if (task == null)
            {
                yield return new LlmStreamChunk { IsDone = true, Error = "task is null" };
                yield break;
            }

            int contextPass = 0;
            int contextOverflowPasses = 0;
            int maxContextOverflowRetries = Math.Max(0, _settings.MaxContextOverflowRetries);

            while (true)
            {
                RequestBundle bundle = await BuildRequestAsync(task, contextPass, cancellationToken)
                    .ConfigureAwait(false);
                StringBuilder accumulated = new();
                int chunkCount = 0;
                string terminalError = null;
                LlmErrorCode terminalErrorCode = LlmErrorCode.None;
                int? terminalHttpStatus = null;
                int? terminalRetryAfterSeconds = null;
                IReadOnlyList<LlmToolCallTrace> executedToolCalls = Array.Empty<LlmToolCallTrace>();
                LlmStreamChunk pendingToolOnlyTerminalChunk = null;
                int? promptTokens = null;
                int? completionTokens = null;
                int? totalTokens = null;
                int cacheReadTokens = 0;
                int cacheWriteTokens = 0;
                LlmCompletionResult contextOverflowFailure = null;

                // Timeout is enforced by the Unity-aware caller (CoreAiChatService)

                LlmCompletionRequest req = BuildCompletionRequest(
                    bundle, task, bundle.UserPayload,
                    ResolveMaxOutputTokens(task.MaxOutputTokens, bundle.RoleConfig.MaxOutputTokens));

                Stopwatch sw = Stopwatch.StartNew();
                IAsyncEnumerator<LlmStreamChunk> enumerator = null;
                string initError = null;
                LlmErrorCode initErrorCode = LlmErrorCode.ProviderError;
                int? initHttpStatus = null;
                int? initRetryAfterSeconds = null;
                try
                {
                    enumerator = _llm.CompleteStreamingAsync(req, cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);
                }
                catch (LlmClientException ex)
                {
                    initError = ex.Message;
                    initErrorCode = ex.ErrorCode;
                    initHttpStatus = ex.HttpStatus;
                    initRetryAfterSeconds = ex.RetryAfterSeconds;
                }
                catch (Exception ex)
                {
                    initError = ex.Message;
                }

                if (initError != null)
                {
                    LlmCompletionResult initFailure = BuildFailureResult(
                        initError,
                        initErrorCode,
                        initHttpStatus,
                        initRetryAfterSeconds);
                    bool canRetryInitOverflow = _compactionCoordinator.ShouldRetryAfterContextOverflow(
                        initFailure,
                        contextOverflowPasses,
                        maxContextOverflowRetries);
                    if (canRetryInitOverflow)
                    {
                        _metrics.RecordLlmCompletion(bundle.RoleId, bundle.TraceId, false, 0d);
                        contextOverflowPasses++;
                        contextPass = contextOverflowPasses;
                        continue;
                    }

                    RecordTrace(bundle, initFailure, null, initError);
                    yield return new LlmStreamChunk
                    {
                        IsDone = true,
                        Error = initError,
                        ErrorCode = initErrorCode,
                        HttpStatus = initHttpStatus,
                        RetryAfterSeconds = initRetryAfterSeconds
                    };
                    yield break;
                }

                try
                {
                    while (true)
                    {
                        bool hasNext;
                        LlmStreamChunk current = null;
                        string exceptionMessage = null;
                        LlmErrorCode exceptionCode = LlmErrorCode.ProviderError;
                        int? exceptionHttpStatus = null;
                        int? exceptionRetryAfterSeconds = null;
                        bool wasCancelled = false;

                        try
                        {
                            // No ConfigureAwait(false): WebGL has no working ThreadPool, and the
                            // continuation must come back through UnitySynchronizationContext.
                            hasNext = await enumerator.MoveNextAsync();
                            current = hasNext ? enumerator.Current : null;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            terminalError = "cancelled";
                            terminalErrorCode = LlmErrorCode.Cancelled;
                            wasCancelled = true;
                            hasNext = false;
                        }
                        catch (LlmClientException ex)
                        {
                            exceptionMessage = ex.Message;
                            exceptionCode = ex.ErrorCode;
                            exceptionHttpStatus = ex.HttpStatus;
                            exceptionRetryAfterSeconds = ex.RetryAfterSeconds;
                            hasNext = false;
                        }
                        catch (Exception ex)
                        {
                            exceptionMessage = ex.Message;
                            hasNext = false;
                        }

                        if (wasCancelled)
                        {
                            yield return new LlmStreamChunk
                            {
                                IsDone = true,
                                Error = terminalError,
                                ErrorCode = terminalErrorCode
                            };
                            yield break;
                        }

                        if (exceptionMessage != null)
                        {
                            terminalError = exceptionMessage;
                            terminalErrorCode = exceptionCode;
                            terminalHttpStatus = exceptionHttpStatus;
                            terminalRetryAfterSeconds = exceptionRetryAfterSeconds;
                            if (exceptionCode == LlmErrorCode.ContextLengthExceeded && chunkCount == 0)
                            {
                                contextOverflowFailure = BuildFailureResult(
                                    exceptionMessage,
                                    exceptionCode,
                                    exceptionHttpStatus,
                                    exceptionRetryAfterSeconds);
                                break;
                            }

                            yield return new LlmStreamChunk
                            {
                                IsDone = true,
                                Error = exceptionMessage,
                                ErrorCode = exceptionCode,
                                HttpStatus = exceptionHttpStatus,
                                RetryAfterSeconds = exceptionRetryAfterSeconds
                            };
                            yield break;
                        }

                        if (!hasNext)
                        {
                            break;
                        }

                        if (current != null && !string.IsNullOrEmpty(current.Text))
                        {
                            accumulated.Append(current.Text);
                            chunkCount++;
                        }

                        if (current != null && !string.IsNullOrEmpty(current.Error))
                        {
                            terminalError = current.Error;
                            terminalErrorCode = current.ErrorCode;
                            terminalHttpStatus = current.HttpStatus;
                            terminalRetryAfterSeconds = current.RetryAfterSeconds;
                            if (current.ErrorCode == LlmErrorCode.ContextLengthExceeded && chunkCount == 0)
                            {
                                contextOverflowFailure = BuildFailureResult(
                                    current.Error,
                                    current.ErrorCode,
                                    current.HttpStatus,
                                    current.RetryAfterSeconds);
                                break;
                            }
                        }

                        if (current?.ExecutedToolCalls != null && current.ExecutedToolCalls.Count > 0)
                        {
                            executedToolCalls = current.ExecutedToolCalls;
                        }

                        if (current?.PromptTokens > 0)
                        {
                            promptTokens = current.PromptTokens;
                        }

                        if (current?.CompletionTokens > 0)
                        {
                            completionTokens = current.CompletionTokens;
                        }

                        if (current?.TotalTokens > 0)
                        {
                            totalTokens = current.TotalTokens;
                        }

                        if (current != null)
                        {
                            if (current.CacheReadTokens > 0)
                            {
                                cacheReadTokens = current.CacheReadTokens;
                            }

                            if (current.CacheWriteTokens > 0)
                            {
                                cacheWriteTokens = current.CacheWriteTokens;
                            }
                        }

                        if (current != null &&
                            current.IsDone &&
                            string.IsNullOrEmpty(current.Error) &&
                            string.IsNullOrWhiteSpace(current.Text) &&
                            current.ExecutedToolCalls != null &&
                            current.ExecutedToolCalls.Count > 0)
                        {
                            pendingToolOnlyTerminalChunk = current;
                            continue;
                        }

                        yield return current;
                    }
                }
                finally
                {
                    sw.Stop();
                    if (enumerator != null)
                    {
                        try
                        {
                            await enumerator.DisposeAsync();
                        }
                        catch
                        {
                            /* swallow */
                        }
                    }

                    _metrics.RecordLlmCompletion(bundle.RoleId, bundle.TraceId,
                        string.IsNullOrEmpty(terminalError),
                        sw.Elapsed.TotalMilliseconds);
                }

                if (contextOverflowFailure != null)
                {
                    bool canRetryContextOverflow = _compactionCoordinator.ShouldRetryAfterContextOverflow(
                        contextOverflowFailure,
                        contextOverflowPasses,
                        maxContextOverflowRetries);
                    if (canRetryContextOverflow)
                    {
                        contextOverflowPasses++;
                        contextPass = contextOverflowPasses;
                        continue;
                    }

                    RecordTrace(bundle, contextOverflowFailure, null, contextOverflowFailure.Error);
                    yield return new LlmStreamChunk
                    {
                        IsDone = true,
                        Error = contextOverflowFailure.Error,
                        ErrorCode = contextOverflowFailure.ErrorCode,
                        HttpStatus = contextOverflowFailure.HttpStatus,
                        RetryAfterSeconds = contextOverflowFailure.RetryAfterSeconds
                    };
                    yield break;
                }

                string content = accumulated.ToString();
                bool synthesizedToolOnlyContent = string.IsNullOrWhiteSpace(content);
                string toolOnlyContent = ResolveToolOnlyCompletionContent(content, executedToolCalls);
                if (!string.IsNullOrEmpty(toolOnlyContent))
                {
                    content = toolOnlyContent;
                    if (synthesizedToolOnlyContent)
                    {
                        yield return new LlmStreamChunk { Text = content };
                    }
                }

                if (string.IsNullOrEmpty(terminalError) && !string.IsNullOrEmpty(content))
                {
                    if (_structuredPolicy.ShouldValidate(bundle.RoleId) &&
                        !_structuredPolicy.TryValidate(bundle.RoleId, content, out string failReason))
                    {
                        _metrics.RecordStructuredRetry(bundle.RoleId, bundle.TraceId, failReason ?? "");
                        yield return new LlmStreamChunk
                        {
                            IsDone = true,
                            Error = "structured validation failed: " + (failReason ?? "")
                        };
                        yield break;
                    }

                    LlmCompletionResult streamResult = new()
                    {
                        Ok = true,
                        Content = content,
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        TotalTokens = totalTokens,
                        CacheReadTokens = cacheReadTokens,
                        CacheWriteTokens = cacheWriteTokens,
                        ExecutedToolCalls = executedToolCalls
                    };
                    // SanitizeAndPublish already records the token-calibration observation from
                    // streamResult.PromptTokens; recording it again here double-applied the EMA and did a
                    // second disk write per streaming turn (the non-streaming path records exactly once).
                    content = SanitizeAndPublish(bundle, task, content, bundle.UserPayload, streamResult);
                }
                else if (!string.IsNullOrEmpty(terminalError))
                {
                    LlmCompletionResult failure = BuildFailureResult(
                        terminalError,
                        terminalErrorCode,
                        terminalHttpStatus,
                        terminalRetryAfterSeconds);
                    RecordTrace(bundle, failure, null, terminalError);
                }

                if (pendingToolOnlyTerminalChunk != null)
                {
                    pendingToolOnlyTerminalChunk.Text = "";
                    yield return pendingToolOnlyTerminalChunk;
                }

                yield break;
            }
        }

        private sealed class RequestBundle
        {
            public string RoleId;
            public string TraceId;
            public GameSessionSnapshot Snapshot;
            public string SystemPrompt;
            public string UserPayload;
            public IReadOnlyList<ILlmTool> Tools;
            public List<Microsoft.Extensions.AI.ChatMessage> ChatHistory;
            public AgentMemoryPolicy.RoleMemoryConfig RoleConfig;
            public AiTaskRequest Task;
            public int ContextWindowTokens;
            public int HistoryTokenBudget;
            public int ChatHistoryMessageCount;
            public int EstimatedPromptTokens;
        }

        private async Task<(string systemPrompt, List<Microsoft.Extensions.AI.ChatMessage> chatHistory, bool wasCompacted)>
            BuildChatHistoryAsync(
                string roleId,
                AgentMemoryPolicy.RoleMemoryConfig roleConfig,
                string system,
                ConversationContextBuildArgs buildArgs,
                string traceId,
                CancellationToken cancellationToken)
        {
            if (!roleConfig.WithChatHistory || _memoryStore == null)
            {
                return (system, null, false);
            }

            int maxMessages = roleConfig.MaxChatHistoryMessages > 0 ? roleConfig.MaxChatHistoryMessages : 30;
            ChatMessage[] history = _memoryStore.GetChatHistory(roleId, maxMessages);
            if (history == null || history.Length == 0)
            {
                return (system, null, false);
            }

            ConversationContextSnapshot snapshot =
                _contextManager is IAsyncConversationContextManager asyncCtx
                    ? await asyncCtx
                        .BuildSnapshotAsync(roleId, history, roleConfig, buildArgs, traceId, cancellationToken)
                        .ConfigureAwait(false)
                    : _contextManager.BuildSnapshot(roleId, history, roleConfig, buildArgs);
            if (snapshot == null)
            {
                return (system, null, false);
            }

            string resultSystem = system;
            bool hasSummary = !string.IsNullOrWhiteSpace(snapshot.Summary);
            string summaryBlock = hasSummary
                ? "## Conversation Summary\n" + snapshot.Summary.Trim()
                : "";

            ChatMessage[] recent = snapshot.RecentMessages ?? Array.Empty<ChatMessage>();
            if (recent.Length == 0)
            {
                if (hasSummary)
                {
                    return (resultSystem, new List<Microsoft.Extensions.AI.ChatMessage>
                    {
                        new(Microsoft.Extensions.AI.ChatRole.System, summaryBlock)
                    }, snapshot.WasCompacted);
                }

                return (resultSystem, null, snapshot.WasCompacted);
            }

            List<Microsoft.Extensions.AI.ChatMessage> chatHistory =
                new(recent.Length + (hasSummary ? 1 : 0));
            if (hasSummary)
            {
                chatHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.System,
                    summaryBlock));
            }

            foreach (ChatMessage msg in recent)
            {
                Microsoft.Extensions.AI.ChatRole aiRole = ResolveChatHistoryRole(msg.Role);
                chatHistory.Add(new Microsoft.Extensions.AI.ChatMessage(aiRole, msg.Content));
            }

            return (resultSystem, chatHistory, snapshot.WasCompacted);
        }

        private static void AppendWorldStateTailMessage(
            ref List<Microsoft.Extensions.AI.ChatMessage> chatHistory,
            string worldState)
        {
            if (string.IsNullOrWhiteSpace(worldState))
            {
                return;
            }

            chatHistory ??= new List<Microsoft.Extensions.AI.ChatMessage>(1);
            chatHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.System,
                "## World State\n" + worldState.Trim()));
        }

        private static void AppendMemoryTailMessage(
            ref List<Microsoft.Extensions.AI.ChatMessage> chatHistory,
            string memoryTailBlock)
        {
            if (string.IsNullOrWhiteSpace(memoryTailBlock))
            {
                return;
            }

            chatHistory ??= new List<Microsoft.Extensions.AI.ChatMessage>(1);
            chatHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.System,
                memoryTailBlock.Trim()));
        }

        private static string AppendTailBudgetSection(string system, string tailBlock)
        {
            if (string.IsNullOrWhiteSpace(tailBlock))
            {
                return system;
            }

            return string.IsNullOrWhiteSpace(system)
                ? tailBlock.Trim()
                : system.TrimEnd() + "\n\n" + tailBlock.Trim();
        }

        private void RecordTrace(RequestBundle bundle, LlmCompletionResult result, string assistantResponse,
            string error)
        {
            if (_traceSink == null || bundle == null)
            {
                return;
            }

            _traceSink.Record(new AgentTurnTrace
            {
                TraceId = bundle.TraceId,
                RoleId = bundle.RoleId,
                RoutingProfileId = "",
                Model = result?.Model ?? "",
                SystemPromptPreview = SingleLine(bundle.SystemPrompt, 4000),
                UserPayload = bundle.UserPayload,
                AssistantResponse = assistantResponse ?? result?.Content ?? "",
                Error = error ?? "",
                PromptTokens = result?.PromptTokens ?? 0,
                CompletionTokens = result?.CompletionTokens ?? 0,
                TotalTokens = result?.TotalTokens ?? 0,
                CacheReadTokens = result?.CacheReadTokens ?? 0,
                CacheWriteTokens = result?.CacheWriteTokens ?? 0,
                HistoryTokenBudget = bundle.HistoryTokenBudget,
                ChatHistoryMessageCount = bundle.ChatHistoryMessageCount
            });
        }

        private void RecordTokenObservation(RequestBundle bundle, LlmCompletionResult result)
        {
            if (result == null || !result.Ok)
            {
                return;
            }

            RecordTokenObservation(bundle, result?.PromptTokens);
        }

        private void RecordTokenObservation(RequestBundle bundle, int? promptTokens)
        {
            if (bundle == null ||
                !_settings.EnableTokenCalibration ||
                bundle.EstimatedPromptTokens <= 0 ||
                promptTokens.GetValueOrDefault() <= 0 ||
                _tokenEstimator is not ICalibratingTokenEstimator calibrating)
            {
                return;
            }

            calibrating.RecordObservation(bundle.EstimatedPromptTokens, promptTokens.Value);
        }

        /// <summary>
        /// ARCH-3 (partial): Shared post-processing for both sync and streaming paths.
        /// Sanitizes tool-call JSON, persists chat history, publishes the game command envelope.
        /// </summary>
        private string SanitizeAndPublish(
            RequestBundle bundle,
            AiTaskRequest task,
            string content,
            string userPayload,
            LlmCompletionResult result)
        {
            // Defense-in-depth: strip leaked tool-call JSON only when tools are actually configured.
            // On plain text roles (no tools), parsing very large reasoning payloads for JSON spans
            // is unnecessary and can stall WebGL UI for a long time.
            if (bundle.Tools != null && bundle.Tools.Count > 0)
            {
                string sanitised = LlmToolCallTextExtractor.StripForDisplay(content);
                if (!string.Equals(sanitised, content, StringComparison.Ordinal))
                {
                    Log.Instance.Warn(
                        $"[AiOrchestrator] role='{bundle.RoleId}' trace='{bundle.TraceId}' tool-call JSON leaked; stripped.",
                        LogTag.Llm);
                    content = sanitised;
                }
            }

            if (bundle.RoleConfig.WithChatHistory && _memoryStore != null)
            {
                _memoryStore.AppendChatMessage(bundle.RoleId, "user", userPayload,
                    bundle.RoleConfig.PersistChatHistory);
                _memoryStore.AppendChatMessage(bundle.RoleId, "assistant", content,
                    bundle.RoleConfig.PersistChatHistory);
                string toolResultsBlock = BuildToolResultsMemoryBlock(
                    result?.ExecutedToolCalls,
                    bundle.RoleConfig.ToolResultMemory);
                if (!string.IsNullOrWhiteSpace(toolResultsBlock))
                {
                    _memoryStore.AppendChatMessage(bundle.RoleId, "tool", toolResultsBlock,
                        bundle.RoleConfig.PersistChatHistory);
                }
            }

            _commandSink.Publish(new ApplyAiGameCommand
            {
                CommandTypeId = Envelope,
                JsonPayload = content,
                SourceRoleId = bundle.RoleId,
                SourceTaskHint = task.Hint ?? "",
                SourceTag = task.SourceTag ?? "",
                LuaRepairGeneration = task.LuaRepairGeneration,
                TraceId = bundle.TraceId,
                LuaScriptVersionKey = task.LuaScriptVersionKey ?? "",
                DataOverlayVersionKeysCsv = task.DataOverlayVersionKeysCsv ?? ""
            });
            _metrics.RecordCommandPublished(bundle.RoleId, bundle.TraceId);
            RecordTokenObservation(bundle, result);
            RecordTrace(bundle, result, content, null);
            return content;
        }

        private static Microsoft.Extensions.AI.ChatRole ResolveChatHistoryRole(string role)
        {
            if (string.Equals(role, "user", StringComparison.Ordinal))
            {
                return Microsoft.Extensions.AI.ChatRole.User;
            }

            if (string.Equals(role, "tool", StringComparison.Ordinal))
            {
                return Microsoft.Extensions.AI.ChatRole.User;
            }

            return Microsoft.Extensions.AI.ChatRole.Assistant;
        }

        private static AiTaskRequest CloneTaskWithStructuredHint(AiTaskRequest task, string failureReason)
        {
            string hint = (task.Hint ?? "").Trim();
            string extra = "structured_retry: " +
                           (string.IsNullOrWhiteSpace(failureReason) ? "(unknown)" : failureReason.Trim());
            if (hint.Length > 0)
            {
                hint += " ";
            }

            hint += extra;
            return new AiTaskRequest
            {
                RoleId = task.RoleId,
                Hint = hint,
                LuaRepairGeneration = task.LuaRepairGeneration,
                LuaRepairPreviousCode = task.LuaRepairPreviousCode,
                LuaRepairErrorMessage = task.LuaRepairErrorMessage,
                TraceId = task.TraceId,
                Priority = task.Priority,
                SourceTag = task.SourceTag,
                CancellationScope = task.CancellationScope,
                LuaScriptVersionKey = task.LuaScriptVersionKey ?? "",
                DataOverlayVersionKeysCsv = task.DataOverlayVersionKeysCsv ?? "",
                ForcedToolMode = task.ForcedToolMode,
                RequiredToolName = task.RequiredToolName ?? "",
                AllowedToolNames = task.AllowedToolNames,
                MaxOutputTokens = task.MaxOutputTokens
            };
        }

        private static IReadOnlyList<ILlmTool> FilterToolsForRequest(IReadOnlyList<ILlmTool> tools, AiTaskRequest task)
        {
            if (tools == null || tools.Count == 0 || task == null)
            {
                return tools;
            }

            if (task.ForcedToolMode == LlmToolChoiceMode.None)
            {
                return Array.Empty<ILlmTool>();
            }

            if (task.AllowedToolNames == null)
            {
                return tools;
            }

            if (task.AllowedToolNames.Length == 0)
            {
                return Array.Empty<ILlmTool>();
            }

            HashSet<string> allowed = new(StringComparer.Ordinal);
            foreach (string name in task.AllowedToolNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    allowed.Add(name.Trim());
                }
            }

            if (allowed.Count == 0)
            {
                return Array.Empty<ILlmTool>();
            }

            List<ILlmTool> filtered = new();
            foreach (ILlmTool tool in tools)
            {
                if (tool != null && allowed.Contains(tool.Name))
                {
                    filtered.Add(tool);
                    continue;
                }

                if (tool is ISkillSetMetaLlmTool skillMetaTool && IntersectsSkillToolAllowlist(skillMetaTool, allowed))
                {
                    filtered.Add(skillMetaTool.RestrictTo(allowed));
                }
            }

            return filtered;
        }

        private static bool IntersectsSkillToolAllowlist(ISkillSetMetaLlmTool skillMetaTool, HashSet<string> allowed)
        {
            if (skillMetaTool == null || allowed == null || allowed.Count == 0)
            {
                return false;
            }

            foreach (string name in allowed)
            {
                if (skillMetaTool.ContainsSkillTool(name))
                {
                    return true;
                }
            }

            return false;
        }

        private string AppendToolContract(
            string system,
            IReadOnlyList<ILlmTool> tools,
            AiTaskRequest task,
            string roleId)
        {
            bool supportsNativeToolCalling = _llm?.SupportsNativeToolCallingForRole(roleId) == true;
            return AiToolContractPromptFormatter.AppendToolContract(
                system,
                tools,
                task,
                _settings,
                supportsNativeToolCalling);
        }

        private static string SingleLine(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (normalized.Contains("  ", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("  ", " ");
            }

            if (maxChars > 0 && normalized.Length > maxChars)
            {
                return normalized.Substring(0, maxChars) + "...";
            }

            return normalized;
        }

        private int EstimateChatHistoryTokens(IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> chatHistory)
        {
            if (chatHistory == null || chatHistory.Count == 0)
            {
                return 0;
            }

            int sum = 0;
            for (int i = 0; i < chatHistory.Count; i++)
            {
                sum += _tokenEstimator.EstimateText(chatHistory[i]?.Text ?? "");
            }

            return sum;
        }

        private int EstimateToolsTokens(IReadOnlyList<ILlmTool> tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return 0;
            }

            int sum = 0;
            for (int i = 0; i < tools.Count; i++)
            {
                ILlmTool tool = tools[i];
                if (tool == null)
                {
                    continue;
                }

                sum += _tokenEstimator.EstimateText(tool.Name ?? "");
                sum += _tokenEstimator.EstimateText(tool.Description ?? "");
                sum += _tokenEstimator.EstimateText(tool.ParametersSchema ?? "");
                sum += 8;
            }

            return sum;
        }

        private static string BuildToolResultsMemoryBlock(
            IReadOnlyList<LlmToolCallTrace> executedToolCalls,
            ToolResultMemoryPolicy policy)
        {
            if (policy == ToolResultMemoryPolicy.None ||
                executedToolCalls == null ||
                executedToolCalls.Count == 0)
            {
                return "";
            }

            StringBuilder sb = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            bool wroteEntry = false;
            sb.AppendLine("## Tool Results");

            for (int i = 0; i < executedToolCalls.Count; i++)
            {
                LlmToolCallTrace trace = executedToolCalls[i];
                if (policy == ToolResultMemoryPolicy.ErrorsOnly && trace.Success)
                {
                    continue;
                }

                string name = string.IsNullOrWhiteSpace(trace.Name) ? "tool" : trace.Name.Trim();
                string normalizedDetail = NormalizeToolResultDetail(trace.Detail);
                string dedupeKey = name + "\n" + normalizedDetail;
                if (!seen.Add(dedupeKey))
                {
                    continue;
                }

                wroteEntry = true;
                string status = trace.Success ? "ok" : "FAILED";
                switch (policy)
                {
                    case ToolResultMemoryPolicy.Full:
                        sb.Append("- ").Append(name).Append(": ").AppendLine(status);
                        sb.AppendLine("  Detail:");
                        sb.AppendLine(IndentToolResultDetail(TruncateHeadTail(normalizedDetail, 2000), "  "));
                        break;
                    case ToolResultMemoryPolicy.ErrorsOnly:
                    case ToolResultMemoryPolicy.CompactSummary:
                    default:
                        string shortDetail = ExtractToolTraceMessage(trace.Detail);
                        shortDetail = SingleLine(shortDetail, 240);
                        sb.Append("- ").Append(name).Append(": ").Append(status);
                        if (!string.IsNullOrWhiteSpace(shortDetail))
                        {
                            sb.Append(' ').Append(shortDetail);
                        }

                        sb.AppendLine();
                        break;
                }
            }

            // Roadmap §7 cross-turn stale tool results are pruned from the prompt copy before compaction.
            return wroteEntry ? sb.ToString().TrimEnd() : "";
        }

        private static string NormalizeToolResultDetail(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return "";
            }

            return detail.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        private static string IndentToolResultDetail(string detail, string indent)
        {
            if (string.IsNullOrEmpty(detail))
            {
                return indent + "(empty)";
            }

            string[] lines = detail.Split('\n');
            StringBuilder sb = new();
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append(indent).Append(lines[i]);
                if (i < lines.Length - 1)
                {
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        private static string TruncateHeadTail(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || maxChars <= 0 || value.Length <= maxChars)
            {
                return value ?? "";
            }

            const string marker = "\n...[truncated]...\n";
            int available = Math.Max(0, maxChars - marker.Length);
            int head = available / 2;
            int tail = available - head;
            return value.Substring(0, head).TrimEnd() +
                   marker +
                   value.Substring(value.Length - tail).TrimStart();
        }

        private static int? ResolveMaxOutputTokens(int? perCall, int? perAgent)
        {
            if (perCall.HasValue && perCall.Value > 0)
            {
                return perCall.Value;
            }

            if (perAgent.HasValue && perAgent.Value > 0)
            {
                return perAgent.Value;
            }

            return null;
        }

        private static AgentMemoryPolicy.RoleMemoryConfig ResolveRoleConfigForRequest(
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            AiTaskRequest task)
        {
            if (!IsChatSourceRequest(task) || roleConfig.WithChatHistory)
            {
                return roleConfig;
            }

            roleConfig.WithChatHistory = true;
            roleConfig.PersistChatHistory = false;
            return roleConfig;
        }

        private static bool IsChatSourceRequest(AiTaskRequest task)
        {
            return string.Equals(task?.SourceTag?.Trim(), "Chat", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveToolOnlyCompletionContent(
            string content,
            IReadOnlyList<LlmToolCallTrace> executedToolCalls)
        {
            if (!string.IsNullOrWhiteSpace(content) ||
                executedToolCalls == null ||
                executedToolCalls.Count == 0)
            {
                return string.IsNullOrWhiteSpace(content) ? null : content;
            }

            List<string> failed = new();
            List<string> succeeded = new();
            for (int i = 0; i < executedToolCalls.Count; i++)
            {
                LlmToolCallTrace trace = executedToolCalls[i];
                string name = string.IsNullOrWhiteSpace(trace.Name) ? "tool" : trace.Name.Trim();
                if (trace.Success)
                {
                    if (!succeeded.Contains(name))
                    {
                        succeeded.Add(name);
                    }

                    continue;
                }

                string detail = ExtractToolTraceMessage(trace.Detail);
                failed.Add(string.IsNullOrWhiteSpace(detail)
                    ? name
                    : $"{name}: {detail}");
            }

            if (failed.Count > 0)
            {
                return failed.Count == 1
                    ? "Tool call failed: " + failed[0]
                    : "Tool calls failed: " + string.Join("; ", failed);
            }

            return succeeded.Count == 1
                ? "Tool call completed: " + succeeded[0] + "."
                : "Tool calls completed: " + string.Join(", ", succeeded) + ".";
        }

        private static string ExtractToolTraceMessage(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return "";
            }

            string trimmed = detail.Trim();
            try
            {
                JObject json = JObject.Parse(trimmed);
                JToken token = json["message"] ?? json["Message"] ?? json["error"] ?? json["Error"];
                if (token != null)
                {
                    string message = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message.Trim();
                    }
                }
            }
            catch
            {
                // Plain-text tool results are expected.
            }

            const int maxChars = 240;
            return trimmed.Length <= maxChars ? trimmed : trimmed.Substring(0, maxChars) + "...";
        }

        private static LlmCompletionResult BuildFailureResult(
            string error,
            LlmErrorCode errorCode,
            int? httpStatus,
            int? retryAfterSeconds)
        {
            return new LlmCompletionResult
            {
                Ok = false,
                Error = error ?? "",
                ErrorCode = errorCode,
                HttpStatus = httpStatus,
                RetryAfterSeconds = retryAfterSeconds
            };
        }

        /// <summary>
        /// ARCH-6: Single source of truth for <see cref="LlmCompletionRequest"/> construction.
        /// Eliminates 3x copy-paste between RunTaskAsync (main + structured retry) and RunStreamingAsync.
        /// </summary>
        private LlmCompletionRequest BuildCompletionRequest(
            RequestBundle bundle,
            AiTaskRequest task,
            string userPayload,
            int? maxOutputTokens)
        {
            return new LlmCompletionRequest
            {
                AgentRoleId = bundle.RoleId,
                SystemPrompt = bundle.SystemPrompt,
                UserPayload = userPayload,
                ChatHistory = bundle.ChatHistory,
                TraceId = bundle.TraceId,
                Tools = bundle.Tools,
                AllowedToolNames = task.AllowedToolNames,
                AllowDuplicateToolCalls = bundle.RoleConfig.AllowDuplicateToolCalls,
                ForcedToolMode = task.ForcedToolMode,
                RequiredToolName = task.RequiredToolName ?? "",
                MaxOutputTokens = maxOutputTokens,
                ContextWindowTokens = bundle.ContextWindowTokens,
                SendTemperature = bundle.RoleConfig.Temperature.HasValue || _settings.OverrideTemperature,
                Temperature = bundle.RoleConfig.Temperature ?? _settings.Temperature
            };
        }

        /// <inheritdoc />
        /// <remarks>No-op here: queueing and scoped cancellation live in <see cref="QueuedAiOrchestrator"/>.</remarks>
        public void CancelTasks(string cancellationScope)
        {
        }

        private static bool IsChatUiSourceTask(AiTaskRequest task)
        {
            return task != null &&
                   string.Equals(task.SourceTag?.Trim(), "Chat", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// For UI chat flows (<see cref="AiTaskRequest.SourceTag"/> = <c>Chat</c>), return a printable error
        /// instead of leaving the orchestrator silent (<c>null</c>), which surfaced as empty UI bubbles.
        /// </summary>
        private static string UserFacingChatFailureOrNull(AiTaskRequest task, string detail)
        {
            if (!IsChatUiSourceTask(task))
            {
                return null;
            }

            string msg = string.IsNullOrWhiteSpace(detail) ? "LLM request failed." : detail.Trim();
            msg = msg.Replace('\r', ' ').Replace('\n', ' ');
            while (msg.Contains("  ", StringComparison.Ordinal))
            {
                msg = msg.Replace("  ", " ");
            }

            if (msg.Length > 400)
            {
                return msg.Substring(0, 400) + "...";
            }

            return msg;
        }
    }
}
