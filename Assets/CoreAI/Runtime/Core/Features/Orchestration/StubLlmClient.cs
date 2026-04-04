using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Детерминированная заглушка для билдов без модели (DGF_SPEC §5.2).
    /// </summary>
    public sealed class StubLlmClient : ILlmClient
    {
        /// <inheritdoc />
        public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            string role = string.IsNullOrWhiteSpace(request.AgentRoleId)
                ? BuiltInAgentRoleIds.Creator
                : request.AgentRoleId.Trim();

            // Чтобы в демо-сцене было видно полный пайплайн оркестрации → Lua → report,
            // Stub для Programmer возвращает валидный fenced-bлок Lua.
            if (role == BuiltInAgentRoleIds.Programmer)
            {
                string payload = "```lua\nreport('stub: lua executed (Programmer)');\n```";
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = payload });
            }

            if (role == BuiltInAgentRoleIds.PlayerChat)
            {
                string reply = "[stub] " + (request.UserPayload ?? "").Trim();
                if (reply.Length > 200)
                {
                    reply = reply.Substring(0, 200) + "…";
                }

                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = reply });
            }

            int userLen = request.UserPayload?.Length ?? 0;
            string modifierJson =
                "{\"commandType\":\"ApplyWaveModifier\",\"payload\":{\"agentRole\":\"" + EscapeJson(role) +
                "\",\"modifierId\":\"stub\",\"wave\":" + userLen + "}}";
            return Task.FromResult(new LlmCompletionResult { Ok = true, Content = modifierJson });
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}