using CoreAI.Session;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Сборка системного промпта через <see cref="IAgentSystemPromptProvider"/> и user через шаблон или дефолт.
    /// </summary>
    public sealed class AiPromptComposer
    {
        private readonly IAgentSystemPromptProvider _systemPrompts;
        private readonly IAgentUserPromptTemplateProvider _userTemplates;

        /// <summary>Создаёт композер с цепочкой провайдеров промптов из DI.</summary>
        public AiPromptComposer(
            IAgentSystemPromptProvider systemPrompts,
            IAgentUserPromptTemplateProvider userTemplates)
        {
            _systemPrompts = systemPrompts;
            _userTemplates = userTemplates;
        }

        /// <summary>Системный промпт для роли (манифест / Resources / встроенный fallback).</summary>
        public string GetSystemPrompt(string roleId)
        {
            if (_systemPrompts.TryGetSystemPrompt(roleId, out var s) && !string.IsNullOrWhiteSpace(s))
                return s.Trim();
            return $"You are agent \"{roleId}\".";
        }

        /// <summary>User-часть для LLM: телеметрия из снимка, hint, шаблон роли, контекст ремонта Lua.</summary>
        public string BuildUserPayload(GameSessionSnapshot snap, AiTaskRequest task)
        {
            var roleId = task.RoleId ?? BuiltInAgentRoleIds.Creator;
            string body;
            if (_userTemplates.TryGetUserTemplate(roleId, out var tmpl))
            {
                // Шаблоны должны быть игронезависимыми: Core не знает про wave/mode/party и т.п.
                // Игра сама определяет ключи телеметрии; Core подставляет только общий JSON и hint.
                var telemetryJson = BuildTelemetryJsonObject(snap);
                body = tmpl
                    .Replace("{telemetry}", telemetryJson)
                    .Replace("{hint}", task.Hint ?? "");
            }
            else
                body = BuildDefaultJsonPayload(snap, task);

            return AppendLuaRepairContext(body, task);
        }

        private static string BuildTelemetryJsonObject(GameSessionSnapshot snap)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            var first = true;
            foreach (var kv in snap.Telemetry)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('\"').Append(EscapeJson(kv.Key)).Append("\":\"").Append(EscapeJson(kv.Value)).Append('\"');
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildDefaultJsonPayload(GameSessionSnapshot snap, AiTaskRequest task)
        {
            // Игронезависимый payload: игра сама выбирает ключи телеметрии и обновляет их в Core;
            // Core только хранит и отдаёт их модели как JSON-словарь.
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"telemetry\":");
            sb.Append(BuildTelemetryJsonObject(snap));
            sb.Append(',');
            sb.Append("\"hint\":\"").Append(EscapeJson(task.Hint ?? "")).Append("\"");
            sb.Append('}');
            return sb.ToString();
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string AppendLuaRepairContext(string body, AiTaskRequest task)
        {
            if (string.IsNullOrEmpty(task.LuaRepairErrorMessage))
                return body;
            var err = ShortenForPrompt(task.LuaRepairErrorMessage, 500);
            var code = ShortenForPrompt(task.LuaRepairPreviousCode ?? "", 1200);
            // repair контекст оставляем строкой-хвостом (универсально и читаемо для LLM),
            // но он не содержит game-specific телеметрии.
            return $"{body}; lua_repair_generation={task.LuaRepairGeneration}; lua_error={err}; fix_this_lua={code}";
        }

        private static string ShortenForPrompt(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
