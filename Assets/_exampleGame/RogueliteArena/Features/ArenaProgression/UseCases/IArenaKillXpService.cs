namespace CoreAI.ExampleGame.ArenaProgression.UseCases
{
    /// <summary>Injected boundary for awarding run XP; the session-scoped replacement for the former static hub.</summary>
    public interface IArenaKillXpService
    {
        /// <summary>Awards the configured base XP for a single enemy kill.</summary>
        void AwardKill();

        /// <summary>Awards an explicit XP amount (e.g. from Lua-driven events).</summary>
        void AwardXp(int amount);
    }
}
