using System;
using System.Collections.Generic;
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
            IAiOrchestrationMetrics metrics)
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
        }

        /// <inheritdoc />
        public async Task RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
            if (!_authority.CanRunAiTasks)
            {
                return;
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
            AgentMemoryPolicy.RoleMemoryConfig roleConfig = _memoryPolicy?.GetRoleConfig(roleId) ?? new AgentMemoryPolicy.RoleMemoryConfig();
            List<Microsoft.Extensions.AI.ChatMessage> chatHistory = null;
            
            if (roleConfig.WithChatHistory && _memoryStore != null)
            {
                ChatMessage[] history = _memoryStore.GetChatHistory(roleId);
                if (history != null && history.Length > 0)
                {
                    chatHistory = new List<Microsoft.Extensions.AI.ChatMessage>(history.Length);
                    foreach (ChatMessage msg in history)
                    {
                        Microsoft.Extensions.AI.ChatRole aiRole = msg.Role == "user" 
                            ? Microsoft.Extensions.AI.ChatRole.User 
                            : Microsoft.Extensions.AI.ChatRole.Assistant;
                        chatHistory.Add(new Microsoft.Extensions.AI.ChatMessage(aiRole, msg.Content));
                    }
                }
            }

            Stopwatch sw = Stopwatch.StartNew();
            LlmCompletionResult result = await _llm.CompleteAsync(
                new LlmCompletionRequest
                {
                    AgentRoleId = roleId,
                    SystemPrompt = system,
                    UserPayload = user,
                    ChatHistory = chatHistory,
                    TraceId = traceId,
                    Tools = tools
                },
                cancellationToken).ConfigureAwait(false);
            sw.Stop();
            _metrics.RecordLlmCompletion(roleId, traceId, result != null && result.Ok, sw.Elapsed.TotalMilliseconds);

            if (result == null || !result.Ok || string.IsNullOrEmpty(result.Content))
            {
                return;
            }

            string content = result.Content;
            if (_structuredPolicy.ShouldValidate(roleId) &&
                !_structuredPolicy.TryValidate(roleId, content, out string failReason))
            {
                _metrics.RecordStructuredRetry(roleId, traceId, failReason ?? "");
                AiTaskRequest retryTask = CloneTaskWithStructuredHint(task, failReason);
                string userRetry = _promptComposer.BuildUserPayload(snap, retryTask);
                sw = Stopwatch.StartNew();
                LlmCompletionResult second = await _llm.CompleteAsync(
                    new LlmCompletionRequest
                    {
                        AgentRoleId = roleId,
                        SystemPrompt = system,
                        UserPayload = userRetry,
                        TraceId = traceId
                    },
                    cancellationToken).ConfigureAwait(false);
                sw.Stop();
                _metrics.RecordLlmCompletion(roleId, traceId, second != null && second.Ok,
                    sw.Elapsed.TotalMilliseconds);

                if (second == null || !second.Ok || string.IsNullOrEmpty(second.Content))
                {
                    return;
                }

                content = second.Content;
                if (!_structuredPolicy.TryValidate(roleId, content, out _))
                {
                    return;
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
    }
}