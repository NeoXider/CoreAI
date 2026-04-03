using CoreAI.ExampleGame.ArenaProgression.UseCases;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    /// <summary>Статические ссылки на текущую сессию прогрессии (один забег на сцену; сбрасывать при уничтожении хоста).</summary>
    public static class ArenaProgressionRuntimeHub
    {
        public static IAddSessionKillXpUseCase AddSessionKillXp { get; set; }
        public static int BaseXpPerKill { get; set; }
        public static int AliveTeamMembersForXp { get; set; } = 1;

        public static void ClearSession()
        {
            AddSessionKillXp = null;
            BaseXpPerKill = 0;
            AliveTeamMembersForXp = 1;
        }
    }
}
