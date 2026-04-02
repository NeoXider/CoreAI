using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Идентификаторы ролей (строки) — синхрон с каталогом AI_AGENT_ROLES.md по смыслу.
    /// Для своих агентов используйте любой стабильный id (например <c>MyGame.Economist</c>).
    /// </summary>
    public static class BuiltInAgentRoleIds
    {
        public const string Creator = "Creator";
        public const string Analyzer = "Analyzer";
        public const string Programmer = "Programmer";
        public const string AiNpc = "AINpc";
        public const string CoreMechanic = "CoreMechanicAI";

        /// <summary>Игровой чат с игроком (как с ассистентом), без обязательного JSON в ответе.</summary>
        public const string PlayerChat = "PlayerChat";

        /// <summary>Все встроенные роли (для тестов и валидации манифестов).</summary>
        public static readonly IReadOnlyList<string> AllBuiltInRoles = new[]
        {
            Creator,
            Analyzer,
            Programmer,
            AiNpc,
            CoreMechanic,
            PlayerChat
        };
    }
}
