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

        public AiOrchestrator(
            IAuthorityHost authority,
            ILlmClient llm,
            IAiGameCommandSink commandSink,
            ISessionTelemetryProvider telemetry,
            AiPromptComposer promptComposer)
        {
            _authority = authority;
            _llm = llm;
            _commandSink = commandSink;
            _telemetry = telemetry;
            _promptComposer = promptComposer;
        }

        public async Task RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
            if (!_authority.CanRunAiTasks)
                return;

            var roleId = string.IsNullOrWhiteSpace(task.RoleId) ? BuiltInAgentRoleIds.Creator : task.RoleId.Trim();
            var snap = _telemetry.BuildSnapshot();
            var system = _promptComposer.GetSystemPrompt(roleId);
            var user = _promptComposer.BuildUserPayload(snap, task);

            var result = await _llm.CompleteAsync(
                new LlmCompletionRequest
                {
                    AgentRoleId = roleId,
                    SystemPrompt = system,
                    UserPayload = user
                },
                cancellationToken).ConfigureAwait(false);

            if (!result.Ok || string.IsNullOrEmpty(result.Content))
                return;

            _commandSink.Publish(new ApplyAiGameCommand
            {
                CommandTypeId = Envelope,
                JsonPayload = result.Content,
                SourceRoleId = roleId,
                SourceTaskHint = task.Hint ?? "",
                LuaRepairGeneration = task.LuaRepairGeneration
            });
        }
    }
}
