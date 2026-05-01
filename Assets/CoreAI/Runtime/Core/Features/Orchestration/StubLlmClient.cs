using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Deterministic LLM stub for model-off builds (DGF_SPEC §5.2).
    /// </summary>
    /// <remarks>
    /// <see cref="BuiltInAgentRoleIds.Programmer"/> returns a fenced Lua block so demo scenes can exercise orchestration → tool → Lua without a model.
    /// </remarks>
    public sealed class StubLlmClient : ILlmClient
    {
        /// <inheritdoc />
        public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            string role = string.IsNullOrWhiteSpace(request.AgentRoleId)
                ? BuiltInAgentRoleIds.Creator
                : request.AgentRoleId.Trim();

            if (role == BuiltInAgentRoleIds.Programmer)
            {
                string payload = "```lua\nreport('stub: lua executed (Programmer)');\n```";
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = payload });
            }

            if (LlmConversationalRolePolicy.IsConversationalUserFacingRole(role))
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = true,
                    Content = "[stub] Offline — LLM unavailable (stub)."
                });
            }

            int userLen = request.UserPayload?.Length ?? 0;

            var payloadObj = new
            {
                commandType = "ApplyWaveModifier",
                payload = new
                {
                    agentRole = role,
                    modifierId = "stub",
                    wave = userLen
                }
            };

            string modifierJson = Newtonsoft.Json.JsonConvert.SerializeObject(payloadObj);
            return Task.FromResult(new LlmCompletionResult { Ok = true, Content = modifierJson });
        }
    }
}
