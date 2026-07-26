using System;
using System.Collections.Generic;
using CoreAI.Logging;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Audit;
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
        /// <summary>
        /// Legacy sentinel for "no history cap". The orchestrator no longer uses it: even with
        /// <see cref="ICoreAISettings.EnableConversationHistorySummarization"/> off the recent tail is
        /// bounded by the policy-computed history budget so long sessions stop growing per-request.
        /// Kept for diagnostics mirrors (see AgentSessionInspector).
        /// </summary>
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
            string systemBase = _promptComposer.GetSystemPrompt(roleId, task?.SystemPrompt);
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
            int? routedWindowTokens =
                _llm?.ResolveContextWindowTokensForRole(roleId, task?.RoutingProfileId ?? "");
            if (routedWindowTokens > 0)
            {
                // WHY: budgets must follow the routed endpoint's real window — after a 128K→8K
                // switch the configured budget would overflow every turn. An explicit per-role
                // budget still applies when it is stricter than the endpoint.
                contextWindowTokens = roleConfig.ContextTokens > 0
                    ? Math.Min(contextWindowTokens, routedWindowTokens.Value)
                    : routedWindowTokens.Value;
            }

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
                // WHY: Disabling summarization used to switch to an effectively unlimited tail
                // (UnlimitedHistoryTokenBudget), so every request re-sent the whole transcript and
                // long sessions got progressively slower until the context window overflowed. The
                // recent tail is now always bounded by the endpoint-derived policy budget (rolling
                // truncation drops the oldest turns); summarization off only skips summary generation.
                int historyBudget = budget.HistoryTokenBudget;
                if (_settings.ConversationHistoryRecentTokenBudgetOverride > 0)
                {
                    historyBudget = Math.Max(32, _settings.ConversationHistoryRecentTokenBudgetOverride);
                }

                // WHY: On a context-overflow retry pass the policy budget is progressively shrunk
                // (0.75^retryLevel). The fixed-override branch above ignores that shrink, so without
                // this clamp a retry would rebuild a byte-identical oversized request that overflows
                // again (up to MaxContextOverflowRetries wasted calls). Bounding by the shrunk
                // policy budget makes each retry actually reduce the prompt — also with summarization
                // disabled, where the retry drops the oldest turns without generating a summary.
                if (contextRetryPass > 0)
                {
                    historyBudget = Math.Min(historyBudget, budget.HistoryTokenBudget);
                }

                float compactionTriggerRatio =
                    roleConfig.CompactionTriggerRatio ?? _settings.ConversationCompactionTriggerRatio;
                ctxBuildArgs = new ConversationContextBuildArgs
                {
                    HistoryTokenBudget = historyBudget,
                    SourceBudget = budget,
                    UseLlmContextCompaction =
                        _settings.EnableLlmContextCompaction && roleConfig.UseLlmContextCompaction,
                    // WHY: 0 is the documented explicit opt-out (unlimited rolling summary); mapping 0 to
                    // the 2048 default here silently truncated installs that chose 0. Fresh installs still
                    // get the cap from the ICoreAISettings interface default (2048).
                    MaxRolledSummaryTokens = _settings.ConversationRolledSummaryMaxTokens,
                    CompactionTriggerRatio = compactionTriggerRatio,
                    EnableContextPruning = _settings.EnableContextPruning,
                    MaxRetainedToolResultMessages = _settings.MaxRetainedToolResultMessages,
                    DeferSummaryPersistence = true
                };
            }

            (string updatedSystem, List<Microsoft.Extensions.AI.ChatMessage> chatHistory, bool wasCompacted,
                    ConversationContextSnapshot contextSnapshot) =
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
            string promptText = (system ?? "") + "\n" + (user ?? "") + "\n" + string.Join("\n",
                (System.Collections.IEnumerable)chatHistory ?? Array.Empty<object>());
            AuditContext.SetPromptHash(traceId, AuditHash.Compute(promptText));
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
                EstimatedPromptTokens = estimatedPromptTokens,
                ContextSnapshot = contextSnapshot
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

            if (task == null)
            {
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

            // WHY: Single invocation for non-context failures; bounded tighter-history rebuilds when the
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
                        // WHY: Streaming by default (CompleteForTaskAsync): task execution runs through the
                        // same execute-as-you-stream tool path as chat when EnableStreaming is on, and
                        // falls back to non-streaming CompleteAsync otherwise. The helper handles the
                        // WebGL SynchronizationContext discipline internally.
                        result = await CompleteForTaskAsync(
                            BuildCompletionRequest(bundle, task, user, maxOutputTokens),
                            cancellationToken);
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
            catch (OperationCanceledException)
            {
                // WHY: A cancellation (incl. TaskCanceledException from a timeout or a linked token) must
                // propagate as cancellation, not fall into the general catch below and collapse to null —
                // a null result is indistinguishable from a genuine empty model response. Re-throw so the
                // caller can tell "cancelled" apart from "the model returned nothing".
                throw;
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
                LlmCompletionResult second = await CompleteForTaskAsync(
                    BuildCompletionRequest(bundle, task, userRetry, maxOutputTokens),
                    cancellationToken);
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
            bundle.ContextSnapshot?.Commit();
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
                int? lastRoundtripPromptTokens = null;
                int? completionTokens = null;
                int? totalTokens = null;
                int cacheReadTokens = 0;
                int cacheWriteTokens = 0;
                LlmCompletionResult contextOverflowFailure = null;

                // WHY: Timeout is enforced by the Unity-aware caller (CoreAiChatService)

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
                            // WHY: No ConfigureAwait(false): WebGL has no working ThreadPool, and the
                            // continuation must come back through UnitySynchronizationContext.
                            hasNext = await enumerator.MoveNextAsync();
                            current = hasNext ? enumerator.Current : null;
                        }
                        catch (OperationCanceledException)
                        {
                            // WHY: The OCE may carry a token other than the caller's (timeout decorator's
                            // linked CTS). Falling through to the generic handler would report it as a
                            // retryable provider fault instead of a cancellation.
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

                        if (current?.LastRoundtripPromptTokens > 0)
                        {
                            lastRoundtripPromptTokens = current.LastRoundtripPromptTokens;
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
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            /* best-effort teardown of the inner enumerator */
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
                        LastRoundtripPromptTokens = lastRoundtripPromptTokens,
                        CompletionTokens = completionTokens,
                        TotalTokens = totalTokens,
                        CacheReadTokens = cacheReadTokens,
                        CacheWriteTokens = cacheWriteTokens,
                        ExecutedToolCalls = executedToolCalls
                    };
                    // WHY: SanitizeAndPublish already records the token-calibration observation from
                    // streamResult.PromptTokens; recording it again here double-applied the EMA and did a
                    // second disk write per streaming turn (the non-streaming path records exactly once).
                    content = SanitizeAndPublish(bundle, task, content, bundle.UserPayload, streamResult);
                    bundle.ContextSnapshot?.Commit();
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

        /// <summary>
        /// Obtains one completion for a task turn. Per the "streaming by default" policy, when
        /// <see cref="ICoreAISettings.EnableStreaming"/> is on this drives
        /// <see cref="ILlmClient.CompleteStreamingAsync"/> — so non-interactive task execution uses the
        /// same execute-as-you-stream tool path (including bounded-parallel tool calls) as chat — and
        /// collapses the streamed chunks into an <see cref="LlmCompletionResult"/>. When streaming is
        /// disabled it falls back to the non-streaming <see cref="ILlmClient.CompleteAsync"/>. The
        /// collapsed result mirrors the accumulation in <see cref="RunStreamingAsync"/>: final assistant
        /// text, token counts, executed tool calls, and any terminal error/code. Callers keep their
        /// existing tool-only-content, context-overflow, and empty-response handling unchanged.
        /// </summary>
        private async Task<LlmCompletionResult> CompleteForTaskAsync(
            LlmCompletionRequest req, CancellationToken cancellationToken)
        {
            if (!_settings.EnableStreaming)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return await _llm.CompleteAsync(req, cancellationToken);
#else
                return await _llm.CompleteAsync(req, cancellationToken).ConfigureAwait(false);
#endif
            }

            StringBuilder accumulated = new();
            string terminalError = null;
            LlmErrorCode terminalErrorCode = LlmErrorCode.None;
            int? terminalHttpStatus = null;
            int? terminalRetryAfterSeconds = null;
            IReadOnlyList<LlmToolCallTrace> executedToolCalls = Array.Empty<LlmToolCallTrace>();
            int? promptTokens = null;
            int? lastRoundtripPromptTokens = null;
            int? completionTokens = null;
            int? totalTokens = null;
            int cacheReadTokens = 0;
            int cacheWriteTokens = 0;

            IAsyncEnumerator<LlmStreamChunk> enumerator;
            try
            {
                enumerator = _llm.CompleteStreamingAsync(req, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (LlmClientException ex)
            {
                return BuildFailureResult(ex.Message, ex.ErrorCode, ex.HttpStatus, ex.RetryAfterSeconds);
            }
            catch (Exception ex)
            {
                return BuildFailureResult(ex.Message, LlmErrorCode.ProviderError, null, null);
            }

            try
            {
                while (true)
                {
                    LlmStreamChunk current;
                    try
                    {
                        // WHY: No ConfigureAwait(false): WebGL has no working ThreadPool, so the continuation
                        // must resume on UnitySynchronizationContext (mirrors RunStreamingAsync).
                        if (!await enumerator.MoveNextAsync())
                        {
                            break;
                        }

                        current = enumerator.Current;
                    }
                    catch (OperationCanceledException)
                    {
                        // WHY: RunTaskAsync distinguishes real cancellation from an empty response, and the
                        // OCE may carry a decorator's linked token rather than the caller's own.
                        throw;
                    }
                    catch (LlmClientException ex)
                    {
                        terminalError = ex.Message;
                        terminalErrorCode = ex.ErrorCode;
                        terminalHttpStatus = ex.HttpStatus;
                        terminalRetryAfterSeconds = ex.RetryAfterSeconds;
                        break;
                    }
                    catch (Exception ex)
                    {
                        terminalError = ex.Message;
                        terminalErrorCode = LlmErrorCode.ProviderError;
                        break;
                    }

                    if (current == null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(current.Text))
                    {
                        accumulated.Append(current.Text);
                    }

                    if (!string.IsNullOrEmpty(current.Error))
                    {
                        terminalError = current.Error;
                        terminalErrorCode = current.ErrorCode;
                        terminalHttpStatus = current.HttpStatus;
                        terminalRetryAfterSeconds = current.RetryAfterSeconds;
                    }

                    if (current.ExecutedToolCalls != null && current.ExecutedToolCalls.Count > 0)
                    {
                        executedToolCalls = current.ExecutedToolCalls;
                    }

                    if (current.PromptTokens > 0)
                    {
                        promptTokens = current.PromptTokens;
                    }

                    if (current.LastRoundtripPromptTokens > 0)
                    {
                        lastRoundtripPromptTokens = current.LastRoundtripPromptTokens;
                    }

                    if (current.CompletionTokens > 0)
                    {
                        completionTokens = current.CompletionTokens;
                    }

                    if (current.TotalTokens > 0)
                    {
                        totalTokens = current.TotalTokens;
                    }

                    if (current.CacheReadTokens > 0)
                    {
                        cacheReadTokens = current.CacheReadTokens;
                    }

                    if (current.CacheWriteTokens > 0)
                    {
                        cacheWriteTokens = current.CacheWriteTokens;
                    }
                }
            }
            finally
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

            if (!string.IsNullOrEmpty(terminalError))
            {
                LlmCompletionResult failure = BuildFailureResult(
                    terminalError, terminalErrorCode, terminalHttpStatus, terminalRetryAfterSeconds);
                failure.ExecutedToolCalls = executedToolCalls;
                return failure;
            }

            return new LlmCompletionResult
            {
                Ok = true,
                Content = accumulated.ToString(),
                PromptTokens = promptTokens,
                LastRoundtripPromptTokens = lastRoundtripPromptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
                CacheReadTokens = cacheReadTokens,
                CacheWriteTokens = cacheWriteTokens,
                ExecutedToolCalls = executedToolCalls
            };
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
            public ConversationContextSnapshot ContextSnapshot;
        }

        private async Task<(string systemPrompt, List<Microsoft.Extensions.AI.ChatMessage> chatHistory, bool
                wasCompacted, ConversationContextSnapshot contextSnapshot)>
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
                return (system, null, false, null);
            }

            int maxMessages = roleConfig.MaxChatHistoryMessages > 0 ? roleConfig.MaxChatHistoryMessages : 30;
            ChatMessage[] history = _memoryStore.GetChatHistory(roleId, maxMessages);
            if (history == null || history.Length == 0)
            {
                return (system, null, false, null);
            }

            ConversationContextSnapshot snapshot;
            if (!_settings.EnableConversationHistorySummarization)
            {
                // WHY: Summarization off only skips summary generation; roadmap §7 pruning and the
                // rolling history token budget must still apply, otherwise pruning silently stops
                // working and the tail grows with session length (progressive per-request latency,
                // eventual context overflow).
                ChatMessage[] retained = history;
                if (buildArgs != null && buildArgs.EnableContextPruning)
                {
                    retained = ConversationHistoryPruner.Prune(retained, buildArgs.MaxRetainedToolResultMessages);
                }

                if (buildArgs != null && buildArgs.HistoryTokenBudget > 0 &&
                    retained != null && retained.Length > 0)
                {
                    (_, List<ChatMessage> recentTail) = ConversationHistoryPartition.PartitionByBudget(
                        retained, _tokenEstimator, buildArgs.HistoryTokenBudget);
                    retained = recentTail.ToArray();
                }

                snapshot = new ConversationContextSnapshot
                {
                    RecentMessages = retained,
                    WasCompacted = false
                };
            }
            else
            {
                snapshot = _contextManager is IAsyncConversationContextManager asyncCtx
                    ? await asyncCtx
                        .BuildSnapshotAsync(roleId, history, roleConfig, buildArgs, traceId, cancellationToken)
                        .ConfigureAwait(false)
                    : _contextManager.BuildSnapshot(roleId, history, roleConfig, buildArgs);
            }

            if (snapshot == null)
            {
                return (system, null, false, null);
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
                    }, snapshot.WasCompacted, snapshot);
                }

                return (resultSystem, null, snapshot.WasCompacted, snapshot);
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

            return (resultSystem, chatHistory, snapshot.WasCompacted, snapshot);
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

            AgentTurnTrace trace = new()
            {
                TraceId = bundle.TraceId,
                RoleId = bundle.RoleId,
                RoutingProfileId = "",
                Model = result?.Model ?? "",
                SystemPromptPreview = SingleLine(bundle.SystemPrompt, 4000),
                UserPayload = bundle.UserPayload,
                AssistantResponse = assistantResponse ?? result?.Content ?? "",
                Error = error ?? "",
                Status = string.IsNullOrWhiteSpace(error) ? AgentTurnStatus.Completed : AgentTurnStatus.Failed,
                PromptTokens = result?.PromptTokens ?? 0,
                CompletionTokens = result?.CompletionTokens ?? 0,
                TotalTokens = result?.TotalTokens ?? 0,
                CacheReadTokens = result?.CacheReadTokens ?? 0,
                CacheWriteTokens = result?.CacheWriteTokens ?? 0,
                HistoryTokenBudget = bundle.HistoryTokenBudget,
                ChatHistoryMessageCount = bundle.ChatHistoryMessageCount
            };

            IReadOnlyList<LlmToolCallTrace> toolCalls = result?.ExecutedToolCalls;
            if (toolCalls != null)
            {
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    LlmToolCallTrace call = toolCalls[i];
                    trace.ToolCalls.Add(new AgentTurnToolCallTrace
                    {
                        Name = call.Name ?? "",
                        Success = call.Success,
                        DurationMs = call.DurationMs,
                        Source = call.Source ?? "",
                        Detail = call.Detail ?? ""
                    });
                }
            }

            _traceSink.Record(trace);
        }

        private void RecordTokenObservation(RequestBundle bundle, LlmCompletionResult result)
        {
            if (result == null || !result.Ok)
            {
                return;
            }

            // WHY: the calibration wants the ACTUAL context width; PromptTokens is the whole-turn
            // cumulative sum for cost telemetry and inflates ~N times on N-roundtrip tool turns.
            RecordTokenObservation(bundle, result?.LastRoundtripPromptTokens ?? result?.PromptTokens);
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
            // WHY: Defense-in-depth: strip leaked tool-call JSON only when tools are actually configured.
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
                // WHY: Persist only the raw user intent, NOT the fully-composed userPayload: the composed payload
                // carries per-turn context (telemetry envelope, Lua-repair, mutation-state) that must not
                // accumulate in history — a stale telemetry snapshot on every turn bloats context and confuses
                // the model about which state is current. Live state is delivered fresh each turn (current
                // payload) and on demand via the game_state tool, so history stays a clean conversation.
                _memoryStore.AppendChatMessage(bundle.RoleId, "user",
                    AppendAttachmentPlaceholders(task.Hint ?? string.Empty, task.Attachments),
                    bundle.RoleConfig.PersistChatHistory);
                // WHY: Persist the assistant turn WITHOUT hidden <think> reasoning: chain-of-thought is
                // per-turn scratch space (observed up to ~16k chars) that bloats the durable store and
                // re-enters every future prompt. The UI clamp is visual only; strip at the source.
                _memoryStore.AppendChatMessage(bundle.RoleId, "assistant", StripReasoningForHistory(content),
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

        /// <summary>
        /// Removes hidden reasoning (<c>&lt;think&gt;...&lt;/think&gt;</c> blocks, orphan close tags and
        /// unterminated blocks) from assistant content before it is persisted into conversation history.
        /// Reuses <see cref="ThinkBlockStreamFilter"/> in one-shot mode so persist-time semantics match
        /// the streaming display filter. Returns the input unchanged when no think markers are present.
        /// </summary>
        internal static string StripReasoningForHistory(string content)
        {
            if (string.IsNullOrEmpty(content) ||
                (content.IndexOf("<think", StringComparison.OrdinalIgnoreCase) < 0 &&
                 content.IndexOf("</think", StringComparison.OrdinalIgnoreCase) < 0))
            {
                return content;
            }

            ThinkBlockStreamFilter filter = new();
            string visible = (filter.ProcessChunk(content) + filter.Flush()).Trim();

            // WHY: A whole-message reasoning blob strips to empty; persist the empty marker rather than
            // the blob — ConversationHistoryPruner already treats reasoning-only assistant turns as
            // droppable, and re-persisting the raw blob was exactly the incident being fixed.
            return visible;
        }

        /// <summary>
        /// Appends compact, byte-free placeholders (e.g. <c>[attachment: hero.png image/png 12 KB]</c>) for each
        /// attachment to the persisted user turn. The chat-history store is text-based, so raw image bytes are
        /// never persisted — the placeholder keeps history serializable while recording that files were sent.
        /// </summary>
        private static string AppendAttachmentPlaceholders(string hint, IReadOnlyList<AiAttachment> attachments)
        {
            if (attachments == null || attachments.Count == 0)
            {
                return hint;
            }

            StringBuilder sb = new(hint ?? "");
            foreach (AiAttachment attachment in attachments)
            {
                if (attachment == null)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(attachment.DescribeForHistory());
            }

            return sb.ToString();
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
                RoutingProfileId = task.RoutingProfileId ?? "",
                Hint = hint,
                Attachments = task.Attachments,
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
            // WHY: resolve against the endpoint the request will actually reach (explicit profile or
            // runtime re-route), so the tool contract follows an endpoint switch mid-session.
            bool supportsNativeToolCalling =
                _llm?.SupportsNativeToolCallingForRole(roleId, task?.RoutingProfileId ?? "") == true;
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

            // WHY: Roadmap §7 cross-turn stale tool results are pruned from the prompt copy before compaction.
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

        // WHY: 0 is meaningful: "explicitly unlimited" wins over the level below and reaches the LLM
        // client as 0, which suppresses the settings MaxTokens fallback (no max_tokens sent — a
        // reasoning model's thinking never eats the answer budget). Negative values = unset.
        private static int? ResolveMaxOutputTokens(int? perCall, int? perAgent)
        {
            if (perCall.HasValue && perCall.Value >= 0)
            {
                return perCall.Value;
            }

            if (perAgent.HasValue && perAgent.Value >= 0)
            {
                return perAgent.Value;
            }

            return null;
        }

        /// <summary>
        /// Resolves the effective tool-call roundtrip override with priority per-call &gt; per-agent.
        /// Like <see cref="ResolveMaxOutputTokens"/>, a value of <c>0</c> is MEANINGFUL here (unlimited),
        /// so only <c>null</c> defers to the next source; <c>null</c> from both = inherit the global setting.
        /// </summary>
        private static int? ResolveMaxToolCallRoundtrips(int? perCall, int? perAgent)
        {
            if (perCall.HasValue)
            {
                return perCall.Value;
            }

            if (perAgent.HasValue)
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
                // WHY: Surface the failure to the user with the tool name(s) and the real reason, symmetric with
                // the success path below. Previously this returned null, so a failed tool-only turn fell
                // through to a misleading generic "LLM request failed." even though the LLM succeeded and a
                // tool failed. The per-tool "name: detail" strings were already built above.
                return failed.Count == 1
                    ? "Tool call failed: " + failed[0] + "."
                    : "Tool calls failed: " + string.Join("; ", failed) + ".";
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
                // WHY: Plain-text tool results are expected.
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
                RoutingProfileId = task.RoutingProfileId ?? "",
                SystemPrompt = bundle.SystemPrompt,
                UserPayload = userPayload,
                ChatHistory = bundle.ChatHistory,
                Attachments = task.Attachments,
                TraceId = bundle.TraceId,
                Tools = bundle.Tools,
                AllowedToolNames = task.AllowedToolNames,
                AllowDuplicateToolCalls = bundle.RoleConfig.AllowDuplicateToolCalls,
                ForcedToolMode = task.ForcedToolMode,
                RequiredToolName = task.RequiredToolName ?? "",
                MaxOutputTokens = maxOutputTokens,
                MaxToolCallRoundtrips = ResolveMaxToolCallRoundtrips(
                    task.MaxToolCallRoundtrips, bundle.RoleConfig.MaxToolCallRoundtrips),
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
