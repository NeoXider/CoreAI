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

            // Чтобы в демо-сцене было видно полный пайплайн оркестрации → Lua → report,
            // Stub для Programmer возвращает валидный fenced-bлок Lua.
            if (role == BuiltInAgentRoleIds.Programmer)
            {
                var payload = "```lua\nreport('stub: lua executed (Programmer)');\n```";
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = payload });
            }

            if (role == BuiltInAgentRoleIds.PlayerChat)
            {
                var reply = "[stub] " + (request.UserPayload ?? "").Trim();
                if (reply.Length > 200)
                    reply = reply.Substring(0, 200) + "…";
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = reply });
            }

            if (role == BuiltInAgentRoleIds.Creator)
            {
                // Stub не должен зависеть от игры-специфичного формата.
                // Берём только стандартизированные поля снапшота из user payload,
                // который собран через AiPromptComposer из GameSessionSnapshot.
                var up = (request.UserPayload ?? "");
                var wave = TryReadInt(up, "wave=", 1);
                var hpCur = TryReadInt(up, "hp_current=", -1);
                var hpMax = TryReadInt(up, "hp_max=", -1);
                var hp01 = (hpCur >= 0 && hpMax > 0) ? (float)hpCur / hpMax : 1f;
                var pressure = hp01 < 0.35f ? 0.7f : hp01 < 0.6f ? 0.9f : 1.1f;
                var enemyCount = (int)(2 + (wave - 1) * 2 * pressure);
                if (enemyCount < 1) enemyCount = 1;

                var planJson =
                    "{\"commandType\":\"ArenaWavePlan\",\"payload\":{" +
                    "\"waveIndex1Based\":" + wave + "," +
                    "\"enemyCount\":" + enemyCount + "," +
                    "\"enemyHpMult\":" + pressure.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "," +
                    "\"enemyDamageMult\":" + pressure.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "," +
                    "\"enemyMoveSpeedMult\":1.00," +
                    "\"spawnIntervalSeconds\":0.45," +
                    "\"spawnRadius\":17.50" +
                    "}}";
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = planJson });
            }

            var userLen = request.UserPayload?.Length ?? 0;
            var modifierJson =
                "{\"commandType\":\"ApplyWaveModifier\",\"payload\":{\"agentRole\":\"" + EscapeJson(role) +
                "\",\"modifierId\":\"stub\",\"wave\":" + userLen + "}}";
            return Task.FromResult(new LlmCompletionResult { Ok = true, Content = modifierJson });
        }

        private static int TryReadInt(string s, string key, int fallback)
        {
            if (string.IsNullOrEmpty(s))
                return fallback;
            var i = s.IndexOf(key, System.StringComparison.OrdinalIgnoreCase);
            if (i < 0)
                return fallback;
            i += key.Length;
            var end = i;
            while (end < s.Length && char.IsDigit(s[end]))
                end++;
            if (end <= i)
                return fallback;
            return int.TryParse(s.Substring(i, end - i), out var v) ? v : fallback;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
