using CoreAI.Session;

namespace CoreAI.Ai
{
    /// <summary>
    /// Сборка системного промпта через <see cref="IAgentSystemPromptProvider"/> и user через шаблон или дефолт.
    /// </summary>
    public sealed class AiPromptComposer
    {
        private readonly IAgentSystemPromptProvider _systemPrompts;
        private readonly IAgentUserPromptTemplateProvider _userTemplates;

        public AiPromptComposer(
            IAgentSystemPromptProvider systemPrompts,
            IAgentUserPromptTemplateProvider userTemplates)
        {
            _systemPrompts = systemPrompts;
            _userTemplates = userTemplates;
        }

        public string GetSystemPrompt(string roleId)
        {
            if (_systemPrompts.TryGetSystemPrompt(roleId, out var s) && !string.IsNullOrWhiteSpace(s))
                return s.Trim();
            return $"You are agent \"{roleId}\".";
        }

        public string BuildUserPayload(GameSessionSnapshot snap, AiTaskRequest task)
        {
            var roleId = task.RoleId ?? BuiltInAgentRoleIds.Creator;
            string body;
            if (_userTemplates.TryGetUserTemplate(roleId, out var tmpl))
            {
                body = tmpl
                    .Replace("{wave}", snap.WaveIndex.ToString())
                    .Replace("{mode}", snap.ModeId ?? "")
                    .Replace("{party}", snap.PartySize.ToString())
                    .Replace("{hint}", task.Hint ?? "");
            }
            else
                body = $"wave={snap.WaveIndex}; mode={snap.ModeId}; party={snap.PartySize}; hp_current={snap.PlayerHpCurrent}; hp_max={snap.PlayerHpMax}; alive_enemies={snap.AliveEnemies}; hint={task.Hint}";

            return AppendLuaRepairContext(body, task);
        }

        private static string AppendLuaRepairContext(string body, AiTaskRequest task)
        {
            if (string.IsNullOrEmpty(task.LuaRepairErrorMessage))
                return body;
            var err = ShortenForPrompt(task.LuaRepairErrorMessage, 500);
            var code = ShortenForPrompt(task.LuaRepairPreviousCode ?? "", 1200);
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
