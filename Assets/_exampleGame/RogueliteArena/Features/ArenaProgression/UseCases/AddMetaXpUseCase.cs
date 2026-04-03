using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;
using Neo.Progression;

namespace CoreAI.ExampleGame.ArenaProgression.UseCases
{
    public sealed class AddMetaXpUseCase : IAddMetaXpUseCase
    {
        private readonly ArenaMetaProgressionState _meta;
        private readonly ArenaRunBalanceConfig _balance;

        public AddMetaXpUseCase(ArenaMetaProgressionState meta, ArenaRunBalanceConfig balance)
        {
            _meta = meta;
            _balance = balance;
        }

        public void Execute(int amount)
        {
            if (_meta == null || amount <= 0)
                return;
            _meta.AddMetaXp(amount);
            LevelCurveDefinition c = _balance?.MetaLevelCurve;
            if (c != null)
                _meta.RecomputeMetaLevel(t => c.EvaluateLevel(t));
        }
    }
}
