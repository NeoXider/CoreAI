using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;
using Neo.Progression;

namespace CoreAI.ExampleGame.ArenaProgression.UseCases
{
    public sealed class AddSessionKillXpUseCase : IAddSessionKillXpUseCase
    {
        private readonly ArenaTeamProgressionState _team;
        private readonly ArenaRunBalanceConfig _balance;

        public AddSessionKillXpUseCase(ArenaTeamProgressionState team, ArenaRunBalanceConfig balance)
        {
            _team = team;
            _balance = balance;
        }

        public void Execute(int baseXpAmount, int aliveTeamMembers)
        {
            if (_team == null || _balance == null)
                return;
            int xp = baseXpAmount;
            if (_balance.DivideXpByAliveTeamMembers)
            {
                int div = System.Math.Max(1, aliveTeamMembers);
                xp = System.Math.Max(1, xp / div);
            }

            _team.AddSessionXp(xp);
            LevelCurveDefinition curve = _balance.SessionLevelCurve;
            if (curve != null)
                _team.SetLevelFromCurve(curve.EvaluateLevel(_team.SessionTotalXp));
        }
    }
}
