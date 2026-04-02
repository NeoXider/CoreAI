using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Ai
{
    public sealed class AiOrchestrator : IAiOrchestrationService
    {
        private readonly IAuthorityHost _authority;
        private readonly ILlmClient _llm;
        private readonly IAiGameCommandSink _commandSink;
        private readonly ISessionTelemetryProvider _telemetry;
        private readonly AiPromptComposer _promptComposer;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly AgentMemoryPolicy _memoryPolicy;

        public AiOrchestrator(
            IAuthorityHost authority,
            ILlmClient llm,
            IAiGameCommandSink commandSink,
            ISessionTelemetryProvider telemetry,
            AiPromptComposer promptComposer,
            IAgentMemoryStore memoryStore,
            AgentMemoryPolicy memoryPolicy)
        {
            _authority = authority;
            _llm = llm;
            _commandSink = commandSink;
            _telemetry = telemetry;
            _promptComposer = promptComposer;
            _memoryStore = memoryStore;
            _memoryPolicy = memoryPolicy;
        }

        public async Task RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
            if (!_authority.CanRunAiTasks)
                return;

            var roleId = string.IsNullOrWhiteSpace(task.RoleId) ? BuiltInAgentRoleIds.Creator : task.RoleId.Trim();
            var traceId = string.IsNullOrWhiteSpace(task.TraceId) ? Guid.NewGuid().ToString("N") : task.TraceId.Trim();
            var snap = _telemetry.BuildSnapshot();
            var systemBase = _promptComposer.GetSystemPrompt(roleId);
            var system = systemBase;
            if (_memoryPolicy != null && _memoryPolicy.IsMemoryEnabled(roleId) &&
                _memoryStore != null && _memoryStore.TryLoad(roleId, out var mem) &&
                !string.IsNullOrWhiteSpace(mem?.Memory))
            {
                system = systemBase.Trim() + "\n\n## Memory\n" + mem.Memory.Trim();
            }
            var user = _promptComposer.BuildUserPayload(snap, task);

            var result = await _llm.CompleteAsync(
                new LlmCompletionRequest
                {
                    AgentRoleId = roleId,
                    SystemPrompt = system,
                    UserPayload = user,
                    TraceId = traceId
                },
                cancellationToken).ConfigureAwait(false);

            if (!result.Ok || string.IsNullOrEmpty(result.Content))
                return;

            var content = result.Content;
            if (_memoryPolicy != null && _memoryPolicy.IsMemoryEnabled(roleId) &&
                _memoryStore != null &&
                AgentMemoryDirectiveParser.TryExtract(content, out var cleaned, out var dir) &&
                dir != null)
            {
                _memoryStore.TryLoad(roleId, out var existing);
                if (dir.Clear)
                {
                    _memoryStore.Clear(roleId);
                }
                else if (!string.IsNullOrWhiteSpace(dir.MemoryText))
                {
                    var state = new AgentMemoryState { LastSystemPrompt = systemBase, Memory = dir.MemoryText };
                    if (dir.Append && !string.IsNullOrWhiteSpace(existing?.Memory))
                        state.Memory = existing.Memory.Trim() + "\n" + dir.MemoryText.Trim();
                    _memoryStore.Save(roleId, state);
                }
                else
                {
                    // Даже если память не меняем, полезно сохранять последний system prompt.
                    _memoryStore.Save(roleId, new AgentMemoryState { LastSystemPrompt = systemBase, Memory = existing?.Memory });
                }

                content = cleaned;
            }

            _commandSink.Publish(new ApplyAiGameCommand
            {
                CommandTypeId = Envelope,
                JsonPayload = content,
                SourceRoleId = roleId,
                SourceTaskHint = task.Hint ?? "",
                LuaRepairGeneration = task.LuaRepairGeneration,
                TraceId = traceId
            });
        }
    }
}
