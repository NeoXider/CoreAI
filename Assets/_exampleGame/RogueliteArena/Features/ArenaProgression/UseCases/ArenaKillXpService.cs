namespace CoreAI.ExampleGame.ArenaProgression.UseCases
{
    /// <summary>Session-scoped kill-XP boundary; carries base XP and alive-team context so call sites need no globals.</summary>
    public sealed class ArenaKillXpService : IArenaKillXpService
    {
        private readonly IAddSessionKillXpUseCase _addSessionKillXp;
        private readonly int _baseXpPerKill;
        private readonly int _aliveTeamMembersForXp;

        public ArenaKillXpService(
            IAddSessionKillXpUseCase addSessionKillXp,
            int baseXpPerKill,
            int aliveTeamMembersForXp)
        {
            _addSessionKillXp = addSessionKillXp;
            _baseXpPerKill = baseXpPerKill;
            _aliveTeamMembersForXp = aliveTeamMembersForXp < 1 ? 1 : aliveTeamMembersForXp;
        }

        public void AwardKill()
        {
            AwardXp(_baseXpPerKill);
        }

        public void AwardXp(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            _addSessionKillXp?.Execute(amount, _aliveTeamMembersForXp);
        }
    }
}
