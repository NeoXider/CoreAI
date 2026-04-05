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

        /// <summary>Игровой чат с игроком (как с ассистентом), без обязательного JSON в ответе.</summary>
        public const string PlayerChat = "PlayerChat";

        /// <summary>Торговец/NPC с инвентарём для продажи предметов игроку.</summary>
        public const string Merchant = "Merchant";

        /// <summary>Все встроенные роли (для тестов и валидации манифестов).</summary>
        public static readonly IReadOnlyList<string> AllBuiltInRoles = new[]
        {
            Creator,
            Analyzer,
            Programmer,
            AiNpc,
            CoreMechanic,
            PlayerChat,
            Merchant
        };
    }
}