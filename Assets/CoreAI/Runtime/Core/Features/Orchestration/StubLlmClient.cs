using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Deterministic fallback LLM client used when no real backend is configured.
    /// </summary>
    /// <remarks>
    /// The canned responses keep editor demos and tests functional without network access,
    /// but they are not intended to simulate model quality.
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
                    Content = "[stub] Offline - LLM unavailable (stub)."
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