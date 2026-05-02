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
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Ai
{
    /// <summary>
    /// Orchestration pipeline: prompts, memory, <see cref="ILlmClient"/> invocation,
    /// optional structured-output retry via <see cref="IRoleStructuredResponsePolicy"/>, command publication.
    /// </summary>
    public sealed class AiOrchestrator : IAiOrchestrationService
    {
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
            systemBase = _promptComposer.AppendRuntimeContext(systemBase, task, roleId, traceId);

            string system = systemBase;
            bool useMemoryTool = _memoryPolicy?.IsMemoryEnabled(roleId) ?? false;
            if (useMemoryTool &&
                _memoryStore != null && _memoryStore.TryLoad(roleId, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem?.Memory))
            {
                system = systemBase.Trim() + "\n\n## Memory\n" + mem.Memory.Trim();
            }

            string user = _promptComposer.BuildUserPayload(snap, task);
            IReadOnlyList<ILlmTool> tools = _memoryPolicy?.GetToolsForRole(roleId);
            tools = FilterToolsForRequest(tools, task);
            system = AppendToolContract(system, tools, task);

            AgentMemoryPolicy.RoleMemoryConfig roleConfig =
                _memoryPolicy?.GetRoleConfig(roleId) ?? new AgentMemoryPolicy.RoleMemoryConfig();

            int contextWindowTokens = roleConfig.ContextTokens > 0
                ? roleConfig.ContextTokens
                : _settings.ContextWindowTokens;

            ConversationContextBuildArgs ctxBuildArgs = null;
            int? resolvedMaxOutput = ResolveMaxOutputTokens(task.MaxOutputTokens, roleConfig.MaxOutputTokens);
            if (roleConfig.WithChatHistory && _memoryStore != null)
            {
                ContextBudgetRequest budgetRequest = new()
                {
                    MaxContextTokens = contextWindowTokens,
                    SystemPrompt = system,
                    UserPayload = user,
                    Tools = tools,
                    MaxOutputTokens = resolvedMaxOutput,
                    ContextRetryLevel = contextRetryPass
                };
                ContextBudget budget = _contextBudgetPolicy.Compute(budgetRequest, _tokenEstimator);
                ctxBuildArgs = new ConversationContextBuildArgs
                {
                    HistoryTokenBudget = budget.HistoryTokenBudget,
                    SourceBudget = budget,
                    UseLlmContextCompaction = _settings.EnableLlmContextCompaction && roleConfig.UseLlmContextCompaction
                };
            }

            (string updatedSystem, List<Microsoft.Extensions.AI.ChatMessage> chatHistory) =
                await BuildChatHistoryAsync(roleId, roleConfig, system, ctxBuildArgs, traceId, cancellationToken)
                    .ConfigureAwait(false);
            system = updatedSystem;

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
                ChatHistoryMessageCount = chatHistory?.Count ?? 0
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
            bool contextCompactionApplied = false;

            // Single invocation for non-context failures; one optional tighter-history rebuild when the
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
                        // queues the resumption to TaskScheduler.Default and the awaiter never resumes —
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

                    if (result != null && result.Ok && !string.IsNullOrEmpty(result.Content))
                    {
                        break;
                    }

                    if (contextCompactionApplied)
                    {
                        RecordTrace(bundle, result, null, result?.Error ?? "empty response");
                        string compactionFail = UserFacingChatFailureOrNull(task, result?.Error ?? "empty response");
                        if (compactionFail != null)
                        {
                            return compactionFail;
                        }

                        return null;
                    }

                    if (result != null &&
                        _compactionCoordinator.ShouldRetryOnceAfterContextOverflow(result,
                            contextCompactionApplied))
                    {
                        contextCompactionApplied = true;
                        contextPass = 1;
                        continue;
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
                    string validFail =
                        UserFacingChatFailureOrNull(task, "Structured response validation failed.");
                    if (validFail != null)
                    {
                        return validFail;
                    }

                    return null;
                }
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

            RequestBundle bundle = await BuildRequestAsync(task, 0, cancellationToken).ConfigureAwait(false);
            System.Text.StringBuilder accumulated = new();
            int chunkCount = 0;
            string terminalError = null;

            // Timeout is enforced by the Unity-aware caller (CoreAiChatService)
            // via UniTask.CancelAfterSlim — compatible with WebGL's PlayerLoop.

            LlmCompletionRequest req = BuildCompletionRequest(
                bundle, task, bundle.UserPayload,
                ResolveMaxOutputTokens(task.MaxOutputTokens, bundle.RoleConfig.MaxOutputTokens));

            Stopwatch sw = Stopwatch.StartNew();
            IAsyncEnumerator<LlmStreamChunk> enumerator = null;
            string initError = null;
            try
            {
                enumerator = _llm.CompleteStreamingAsync(req, cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception ex)
            {
                initError = ex.Message;
            }

            if (initError != null)
            {
                yield return new LlmStreamChunk { IsDone = true, Error = initError };
                yield break;
            }

            try
            {
                while (true)
                {
                    bool hasNext;
                    LlmStreamChunk current = null;
                    string exceptionMessage = null;
                    bool wasCancelled = false;

                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        current = hasNext ? enumerator.Current : null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        terminalError = "cancelled";
                        wasCancelled = true;
                        hasNext = false;
                    }
                    catch (Exception ex)
                    {
                        exceptionMessage = ex.Message;
                        hasNext = false;
                    }

                    if (wasCancelled)
                    {
                        yield return new LlmStreamChunk { IsDone = true, Error = terminalError };
                        yield break;
                    }

                    if (exceptionMessage != null)
                    {
                        terminalError = exceptionMessage;
                        yield return new LlmStreamChunk { IsDone = true, Error = exceptionMessage };
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
                    }

                    yield return current;
                }
            }
            finally
            {
                sw.Stop();
                if (enumerator != null)
                {
                    try { await enumerator.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
                }

                _metrics.RecordLlmCompletion(bundle.RoleId, bundle.TraceId,
                    string.IsNullOrEmpty(terminalError),
                    sw.Elapsed.TotalMilliseconds);
            }

            string content = accumulated.ToString();
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

                content = SanitizeAndPublish(bundle, task, content, bundle.UserPayload, null);
            }
            else if (!string.IsNullOrEmpty(terminalError))
            {
                RecordTrace(bundle, null, null, terminalError);
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
        }

        private async Task<(string systemPrompt, List<Microsoft.Extensions.AI.ChatMessage> chatHistory)> BuildChatHistoryAsync(
            string roleId,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            string system,
            ConversationContextBuildArgs buildArgs,
            string traceId,
            CancellationToken cancellationToken)
        {
            if (!roleConfig.WithChatHistory || _memoryStore == null)
            {
                return (system, null);
            }

            int maxMessages = roleConfig.MaxChatHistoryMessages > 0 ? roleConfig.MaxChatHistoryMessages : 30;
            ChatMessage[] history = _memoryStore.GetChatHistory(roleId, maxMessages);
            if (history == null || history.Length == 0)
            {
                return (system, null);
            }

            ConversationContextSnapshot snapshot =
                _contextManager is IAsyncConversationContextManager asyncCtx
                    ? await asyncCtx
                        .BuildSnapshotAsync(roleId, history, roleConfig, buildArgs, traceId, cancellationToken)
                        .ConfigureAwait(false)
                    : _contextManager.BuildSnapshot(roleId, history, roleConfig, buildArgs);
            if (snapshot == null)
            {
                return (system, null);
            }

            string resultSystem = system;
            if (!string.IsNullOrWhiteSpace(snapshot.Summary))
            {
                resultSystem = resultSystem.Trim() + "\n\n## Conversation Summary\n" + snapshot.Summary.Trim();
            }

            ChatMessage[] recent = snapshot.RecentMessages ?? Array.Empty<ChatMessage>();
            if (recent.Length == 0)
            {
                return (resultSystem, null);
            }

            List<Microsoft.Extensions.AI.ChatMessage> chatHistory =
                new(recent.Length);
            foreach (ChatMessage msg in recent)
            {
                Microsoft.Extensions.AI.ChatRole aiRole = msg.Role == "user"
                    ? Microsoft.Extensions.AI.ChatRole.User
                    : Microsoft.Extensions.AI.ChatRole.Assistant;
                chatHistory.Add(new Microsoft.Extensions.AI.ChatMessage(aiRole, msg.Content));
            }

            return (resultSystem, chatHistory);
        }

        private void RecordTrace(RequestBundle bundle, LlmCompletionResult result, string assistantResponse, string error)
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
                HistoryTokenBudget = bundle.HistoryTokenBudget,
                ChatHistoryMessageCount = bundle.ChatHistoryMessageCount
            });
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
            RecordTrace(bundle, result, content, null);
            return content;
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
                }
            }

            return filtered;
        }

        private static string AppendToolContract(
            string system,
            IReadOnlyList<ILlmTool> tools,
            AiTaskRequest task)
        {
            if (tools == null || tools.Count == 0)
            {
                return system;
            }

            StringBuilder sb = new();
            sb.Append(string.IsNullOrWhiteSpace(system) ? "" : system.Trim());
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("## Tool Contract");
            sb.AppendLine("You have native tool-calling available for this role. When the user/task asks to use or call a tool, call the matching tool through the tool interface; do not claim that the tool is unavailable, and do not simulate successful execution in prose.");
            sb.AppendLine("Pass arguments as structured tool arguments matching the schema. Required values mentioned in the task (for example targetName, itemName, quantity, action) must be passed as tool arguments, not only described in text.");
            sb.AppendLine("After a tool succeeds, summarize the real tool result briefly for the user.");

            if (task != null && task.ForcedToolMode == LlmToolChoiceMode.RequireSpecific &&
                !string.IsNullOrWhiteSpace(task.RequiredToolName))
            {
                sb.AppendLine($"This request requires calling tool '{task.RequiredToolName.Trim()}'.");
            }
            else if (task != null && task.ForcedToolMode == LlmToolChoiceMode.RequireAny)
            {
                sb.AppendLine("This request requires calling at least one available tool.");
            }

            sb.AppendLine("Available tools:");
            foreach (ILlmTool tool in tools)
            {
                if (tool == null || string.IsNullOrWhiteSpace(tool.Name))
                {
                    continue;
                }

                sb.Append("- ");
                sb.Append(tool.Name.Trim());
                if (!string.IsNullOrWhiteSpace(tool.Description))
                {
                    sb.Append(": ");
                    sb.Append(SingleLine(tool.Description, 500));
                }
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(tool.ParametersSchema) && tool.ParametersSchema.Trim() != "{}")
                {
                    sb.Append("  schema: ");
                    sb.AppendLine(SingleLine(tool.ParametersSchema, 800));
                }
            }

            return sb.ToString();
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
                ContextWindowTokens = bundle.ContextWindowTokens
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
                return msg.Substring(0, 400) + "…";
            }

            return msg;
        }
    }
}