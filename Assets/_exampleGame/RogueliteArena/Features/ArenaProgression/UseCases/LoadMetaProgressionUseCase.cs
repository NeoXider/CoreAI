using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;
using Neo.Progression;

namespace CoreAI.ExampleGame.ArenaProgression.UseCases
{
    public sealed class LoadMetaProgressionUseCase : ILoadMetaProgressionUseCase
    {
        private readonly ArenaMetaProgressionState _meta;
        private readonly ArenaMetaSaveGateway _gateway;
        private readonly ArenaRunBalanceConfig _balance;

        public LoadMetaProgressionUseCase(
            ArenaMetaProgressionState meta,
            ArenaMetaSaveGateway gateway,
            ArenaRunBalanceConfig balance)
        {
            _meta = meta;
            _gateway = gateway;
            _balance = balance;
        }

        public void Execute()
        {
            _gateway?.LoadInto(_meta);
            LevelCurveDefinition c = _balance?.MetaLevelCurve;
            if (c != null && _meta != null)
                _meta.RecomputeMetaLevel(total => c.EvaluateLevel(total));
        }
    }
}
