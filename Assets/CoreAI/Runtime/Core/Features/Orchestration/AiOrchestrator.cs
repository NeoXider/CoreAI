using System;
using System.Collections.Generic;
using CoreAI.Logging;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Ai
{
    /// <summary>
    /// Реализация пайплайна оркестрации: промпты, память, вызов <see cref="ILlmClient"/>,
    /// опционально один повтор при <see cref="IRoleStructuredResponsePolicy"/>, публикация конверта в шину.
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

        /// <summary>Собирает зависимости оркестратора (регистрация через VContainer).</summary>
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
            ICoreAISettings settings)
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
        }

        /// <summary>
        /// Выделенная сборка запроса (переиспользуется в <see cref="RunTaskAsync"/>
        /// и <see cref="RunStreamingAsync"/>).
        /// </summary>
        private RequestBundle BuildRequest(AiTaskRequest task)
        {
            string roleId = string.IsNullOrWhiteSpace(task.RoleId) ? BuiltInAgentRoleIds.Creator : task.RoleId.Trim();
            string traceId = string.IsNullOrWhiteSpace(task.TraceId)
                ? Guid.NewGuid().ToString("N")
                : task.TraceId.Trim();
            GameSessionSnapshot snap = _telemetry.BuildSnapshot();
            string systemBase = _promptComposer.GetSystemPrompt(roleId);

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

            AgentMemoryPolicy.RoleMemoryConfig roleConfig =
                _memoryPolicy?.GetRoleConfig(roleId) ?? new AgentMemoryPolicy.RoleMemoryConfig();
            List<Microsoft.Extensions.AI.ChatMessage> chatHistory = null;

            if (roleConfig.WithChatHistory && _memoryStore != null)
            {
                ChatMessage[] history = _memoryStore.GetChatHistory(roleId,
                    roleConfig.MaxChatHistoryMessages > 0 ? roleConfig.MaxChatHistoryMessages : 30);
                if (history != null && history.Length > 0)
                {
                    chatHistory = new List<Microsoft.Extensions.AI.ChatMessage>(history.Length);
                    int maxTokens = roleConfig.ContextTokens > 0 ? roleConfig.ContextTokens : 8192;
                    int budgetTokens = maxTokens / 2;

                    List<ChatMessage> filteredHistory = new();
                    for (int i = history.Length - 1; i >= 0; i--)
                    {
                        int estimatedTokens = string.IsNullOrEmpty(history[i].Content) ? 0 : history[i].Content.Length / 3;
                        if (budgetTokens - estimatedTokens < 0 && filteredHistory.Count > 0)
                        {
                            break;
                        }

                        budgetTokens -= estimatedTokens;
                        filteredHistory.Insert(0, history[i]);
                    }

                    foreach (ChatMessage msg in filteredHistory)
                    {
                        Microsoft.Extensions.AI.ChatRole aiRole = msg.Role == "user"
                            ? Microsoft.Extensions.AI.ChatRole.User
                            : Microsoft.Extensions.AI.ChatRole.Assistant;
                        chatHistory.Add(new Microsoft.Extensions.AI.ChatMessage(aiRole, msg.Content));
                    }
                }
            }

            return new RequestBundle
            {
                RoleId = roleId,
                TraceId = traceId,
                SystemPrompt = system,
                UserPayload = user,
                Tools = tools,
                ChatHistory = chatHistory,
                RoleConfig = roleConfig,
                Task = task
            };
        }

        /// <inheritdoc />
        public async Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
            if (!_authority.CanRunAiTasks)
            {
                return null;
            }

            string roleId = string.IsNullOrWhiteSpace(task.RoleId) ? BuiltInAgentRoleIds.Creator : task.RoleId.Trim();
            string traceId = string.IsNullOrWhiteSpace(task.TraceId)
                ? Guid.NewGuid().ToString("N")
                : task.TraceId.Trim();
            GameSessionSnapshot snap = _telemetry.BuildSnapshot();
            string systemBase = _promptComposer.GetSystemPrompt(roleId);

            // ===== ТИП 1: MemoryTool — явная память через function call =====
            string system = systemBase;
            bool useMemoryTool = _memoryPolicy?.IsMemoryEnabled(roleId) ?? false;
            if (useMemoryTool &&
                _memoryStore != null && _memoryStore.TryLoad(roleId, out AgentMemoryState mem) &&
                !string.IsNullOrWhiteSpace(mem?.Memory))
            {
                system = systemBase.Trim() + "\n\n## Memory\n" + mem.Memory.Trim();
            }

            string user = _promptComposer.BuildUserPayload(snap, task);

            // Get tools for this role (includes MemoryTool if enabled)
            // MeaiToolsLlmClientDecorator will inject them into system prompt automatically
            IReadOnlyList<ILlmTool> tools = _memoryPolicy?.GetToolsForRole(roleId);

            // ===== ТИП 2: ChatHistory — автоматическая история чата =====
            AgentMemoryPolicy.RoleMemoryConfig roleConfig =
                _memoryPolicy?.GetRoleConfig(roleId) ?? new AgentMemoryPolicy.RoleMemoryConfig();
            List<Microsoft.Extensions.AI.ChatMessage> chatHistory = null;

            if (roleConfig.WithChatHistory && _memoryStore != null)
            {
                ChatMessage[] history = _memoryStore.GetChatHistory(roleId, roleConfig.MaxChatHistoryMessages > 0 ? roleConfig.MaxChatHistoryMessages : 30);
                if (history != null && history.Length > 0)
                {
                    chatHistory = new List<Microsoft.Extensions.AI.ChatMessage>(history.Length);
                    int maxTokens = roleConfig.ContextTokens > 0 ? roleConfig.ContextTokens : 8192;
                    int budgetTokens = maxTokens / 2; // Резервируем половину бюджета для системного промпта и ответа модели

                    List<ChatMessage> filteredHistory = new List<ChatMessage>();
                    // Идем с конца (от свежих к старым), чтобы оставить самые актуальные
                    for (int i = history.Length - 1; i >= 0; i--)
                    {
                        int estimatedTokens = string.IsNullOrEmpty(history[i].Content) ? 0 : history[i].Content.Length / 3;
                        if (budgetTokens - estimatedTokens < 0 && filteredHistory.Count > 0)
                        {
                            break; // Лимит контекста исчерпан
                        }
                        
                        budgetTokens -= estimatedTokens;
                        filteredHistory.Insert(0, history[i]); // Вставляем в начало, восстанавливая порядок
                    }

                    foreach (ChatMessage msg in filteredHistory)
                    {
                        Microsoft.Extensions.AI.ChatRole aiRole = msg.Role == "user"
                            ? Microsoft.Extensions.AI.ChatRole.User
                            : Microsoft.Extensions.AI.ChatRole.Assistant;
                        chatHistory.Add(new Microsoft.Extensions.AI.ChatMessage(aiRole, msg.Content));
                    }
                }
            }

            int maxAttempts = _settings.MaxLlmRequestRetries > 0 ? _settings.MaxLlmRequestRetries : 2;
            LlmCompletionResult result = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using CancellationTokenSource delayCts = new();
                if (_settings.LlmRequestTimeoutSeconds > 0)
                {
                    delayCts.CancelAfter(TimeSpan.FromSeconds(_settings.LlmRequestTimeoutSeconds));
                }

                using CancellationTokenSource linkedCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, delayCts.Token);

                try
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    result = await _llm.CompleteAsync(
                        new LlmCompletionRequest
                        {
                            AgentRoleId = roleId,
                            SystemPrompt = system,
                            UserPayload = user,
                            ChatHistory = chatHistory,
                            TraceId = traceId,
                            Tools = tools,
                            AllowDuplicateToolCalls = _memoryPolicy?.GetRoleConfig(roleId).AllowDuplicateToolCalls
                        },
                        linkedCts.Token);
                    sw.Stop();
                    _metrics.RecordLlmCompletion(roleId, traceId, result != null && result.Ok,
                        sw.Elapsed.TotalMilliseconds);

                    if (result != null && result.Ok)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (delayCts.IsCancellationRequested &&
                                                         !cancellationToken.IsCancellationRequested)
                {
                    if (attempt == maxAttempts)
                    {
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    Log.Instance.Error($"[AiOrchestrator] Task execution failed (attempt {attempt}/{maxAttempts}): {ex.Message}", LogTag.Llm);
                    if (attempt == maxAttempts)
                    {
                        return null;
                    }
                }
            }

            if (result == null || !result.Ok || string.IsNullOrEmpty(result.Content))
            {
                return null;
            }

            string content = result.Content;
            if (_structuredPolicy.ShouldValidate(roleId) &&
                !_structuredPolicy.TryValidate(roleId, content, out string failReason))
            {
                _metrics.RecordStructuredRetry(roleId, traceId, failReason ?? "");
                AiTaskRequest retryTask = CloneTaskWithStructuredHint(task, failReason);
                string userRetry = _promptComposer.BuildUserPayload(snap, retryTask);
                Stopwatch sw = Stopwatch.StartNew();
                LlmCompletionResult second = await _llm.CompleteAsync(
                    new LlmCompletionRequest
                    {
                        AgentRoleId = roleId,
                        SystemPrompt = system,
                        UserPayload = userRetry,
                        TraceId = traceId,
                        AllowDuplicateToolCalls = _memoryPolicy?.GetRoleConfig(roleId).AllowDuplicateToolCalls
                    },
                    cancellationToken).ConfigureAwait(false);
                sw.Stop();
                _metrics.RecordLlmCompletion(roleId, traceId, second != null && second.Ok,
                    sw.Elapsed.TotalMilliseconds);

                if (second == null || !second.Ok || string.IsNullOrEmpty(second.Content))
                {
                    return null;
                }

                content = second.Content;
                if (!_structuredPolicy.TryValidate(roleId, content, out _))
                {
                    return null;
                }
            }

            // ===== ОБРАБОТКА MemoryTool через MEAI function calling =====
            // Память уже сохранена через FunctionInvokingChatClient -> MemoryTool.ExecuteAsync()
            // Дополнительный fallback парсинг не нужен - всё через единый MEAI pipeline

            if (roleConfig.WithChatHistory && _memoryStore != null)
            {
                _memoryStore.AppendChatMessage(roleId, "user", user, roleConfig.PersistChatHistory);
                _memoryStore.AppendChatMessage(roleId, "assistant", content, roleConfig.PersistChatHistory);
            }

            _commandSink.Publish(new ApplyAiGameCommand
            {
                CommandTypeId = Envelope,
                JsonPayload = content,
                SourceRoleId = roleId,
                SourceTaskHint = task.Hint ?? "",
                SourceTag = task.SourceTag ?? "",
                LuaRepairGeneration = task.LuaRepairGeneration,
                TraceId = traceId,
                LuaScriptVersionKey = task.LuaScriptVersionKey ?? "",
                DataOverlayVersionKeysCsv = task.DataOverlayVersionKeysCsv ?? ""
            });
            _metrics.RecordCommandPublished(roleId, traceId);
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

            RequestBundle bundle = BuildRequest(task);
            System.Text.StringBuilder accumulated = new();
            int chunkCount = 0;
            string terminalError = null;

            using CancellationTokenSource timeoutCts = new();
            if (_settings.LlmRequestTimeoutSeconds > 0)
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.LlmRequestTimeoutSeconds));
            }

            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            LlmCompletionRequest req = new()
            {
                AgentRoleId = bundle.RoleId,
                SystemPrompt = bundle.SystemPrompt,
                UserPayload = bundle.UserPayload,
                ChatHistory = bundle.ChatHistory,
                TraceId = bundle.TraceId,
                Tools = bundle.Tools,
                AllowDuplicateToolCalls = bundle.RoleConfig.AllowDuplicateToolCalls
            };

            Stopwatch sw = Stopwatch.StartNew();
            IAsyncEnumerator<LlmStreamChunk> enumerator = null;
            string initError = null;
            try
            {
                enumerator = _llm.CompleteStreamingAsync(req, linkedCts.Token).GetAsyncEnumerator(linkedCts.Token);
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
                    bool failedTimeout = false;
                    string exceptionMessage = null;

                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        current = hasNext ? enumerator.Current : null;
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested &&
                                                              !cancellationToken.IsCancellationRequested)
                    {
                        failedTimeout = true;
                        hasNext = false;
                    }
                    catch (Exception ex)
                    {
                        exceptionMessage = ex.Message;
                        hasNext = false;
                    }

                    if (failedTimeout)
                    {
                        terminalError = $"orchestrator stream timeout ({_settings.LlmRequestTimeoutSeconds}s)";
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

            // После окончания стрима — структурная валидация и публикация команды.
            string content = accumulated.ToString();
            if (string.IsNullOrEmpty(terminalError) && !string.IsNullOrEmpty(content))
            {
                if (_structuredPolicy.ShouldValidate(bundle.RoleId) &&
                    !_structuredPolicy.TryValidate(bundle.RoleId, content, out string failReason))
                {
                    // Стриминг невозможно «переиграть» без второго стрима,
                    // поэтому просто сообщаем ошибку валидации наружу.
                    _metrics.RecordStructuredRetry(bundle.RoleId, bundle.TraceId, failReason ?? "");
                    yield return new LlmStreamChunk
                    {
                        IsDone = true,
                        Error = "structured validation failed: " + (failReason ?? "")
                    };
                    yield break;
                }

                if (bundle.RoleConfig.WithChatHistory && _memoryStore != null)
                {
                    _memoryStore.AppendChatMessage(bundle.RoleId, "user", bundle.UserPayload,
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
            }
        }

        private sealed class RequestBundle
        {
            public string RoleId;
            public string TraceId;
            public string SystemPrompt;
            public string UserPayload;
            public IReadOnlyList<ILlmTool> Tools;
            public List<Microsoft.Extensions.AI.ChatMessage> ChatHistory;
            public AgentMemoryPolicy.RoleMemoryConfig RoleConfig;
            public AiTaskRequest Task;
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
                DataOverlayVersionKeysCsv = task.DataOverlayVersionKeysCsv ?? ""
            };
        }

        /// <inheritdoc />
        public void CancelTasks(string cancellationScope)
        {
            // AiOrchestrator не управляет очередью и токенами (это делает QueuedAiOrchestrator),
            // поэтому здесь метод пустой.
        }
    }
}