using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Идентификаторы ролей (строки) — синхрон с каталогом AI_AGENT_ROLES.md по смыслу.
    /// Для своих агентов используйте любой стабильный id (например <c>MyGame.Economist</c>).
    /// </summary>
    public static class BuiltInAgentRoleIds
    {
        /// <summary>Процедурный дизайн / контент (волны, модификаторы и т.д.).</summary>
        public const string Creator = "Creator";

        /// <summary>Аналитика состояния сессии без прямого изменения мира.</summary>
        public const string Analyzer = "Analyzer";

        /// <summary>Генерация и исполнение Lua в песочнице.</summary>
        public const string Programmer = "Programmer";

        /// <summary>Диалоги и поведение NPC.</summary>
        public const string AiNpc = "AINpc";

        /// <summary>Ядро правил тайтла (мета-логика).</summary>
        public const string CoreMechanic = "CoreMechanicAI";

        /// <summary>Простой чат с игроком без MemoryTool по умолчанию (история диалога сохраняется).</summary>
        public const string PlainChat = "PlainChat";

        /// <summary>Умный чат с игроком: чат + MemoryTool по умолчанию.</summary>
        public const string SmartChat = "SmartChat";

        /// <summary>Торговец/NPC с инвентарём для продажи предметов игроку.</summary>
        public const string Merchant = "Merchant";

        /// <summary>
        /// Auxiliary routing-only id for transcript compaction completions (never a playable agent).
        /// Host routing may steer this toward a lighter model profile.
        /// </summary>
        public const string ContextCompactionAux = "__CoreAI_ContextCompaction";

        /// <summary>Все встроенные роли (для тестов и валидации манифестов).</summary>
        public static readonly IReadOnlyList<string> AllBuiltInRoles = new[]
        {
            Creator,
            Analyzer,
            Programmer,
            AiNpc,
            CoreMechanic,
            PlainChat,
            SmartChat,
            Merchant
        };

        /// <summary>
        /// Returns <c>true</c> when <paramref name="roleId"/> matches one of the built-in roles
        /// listed in <see cref="AllBuiltInRoles"/>. Built-in roles always have a manifest/Resources
        /// fallback prompt, so callers (e.g. <c>AgentBuilder</c> validation) can skip
        /// "missing system prompt" warnings for them.
        /// </summary>
        public static bool IsBuiltIn(string roleId)
        {
            if (string.IsNullOrEmpty(roleId))
            {
                return false;
            }

            for (int i = 0; i < AllBuiltInRoles.Count; i++)
            {
                if (string.Equals(AllBuiltInRoles[i], roleId, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}