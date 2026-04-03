using System.Collections.Generic;
using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;

namespace CoreAI.ExampleGame.ArenaProgression.UseCases
{
    public sealed class RollUpgradeOffersUseCase : IRollUpgradeOffersUseCase
    {
        private readonly ArenaTeamProgressionState _team;
        private readonly ArenaUpgradeRollService _roll;

        public RollUpgradeOffersUseCase(ArenaTeamProgressionState team, ArenaUpgradeRollService roll)
        {
            _team = team;
            _roll = roll;
        }

        public bool Execute(HashSet<string> excludeIds, List<ArenaUpgradeOffer> into) =>
            _roll != null && _roll.TryRollOffers(_team, excludeIds, into);
    }
}
