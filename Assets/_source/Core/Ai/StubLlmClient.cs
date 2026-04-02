using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Детерминированная заглушка для билдов без модели (DGF_SPEC §5.2).
    /// </summary>
    public sealed class StubLlmClient : ILlmClient
    {
        public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
        {
            var role = string.IsNullOrWhiteSpace(request.AgentRoleId)
                ? BuiltInAgentRoleIds.Creator
                : request.AgentRoleId.Trim();

            if (role == BuiltInAgentRoleIds.PlayerChat)
            {
                var reply = "[stub] " + (request.UserPayload ?? "").Trim();
                if (reply.Length > 200)
                    reply = reply.Substring(0, 200) + "…";
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = reply });
            }

            var userLen = request.UserPayload?.Length ?? 0;
            var json =
                "{\"commandType\":\"ApplyWaveModifier\",\"payload\":{\"agentRole\":\"" + EscapeJson(role) +
                "\",\"modifierId\":\"stub\",\"wave\":" + userLen + "}}";
            return Task.FromResult(new LlmCompletionResult { Ok = true, Content = json });
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
