using System.Collections.Generic;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;

namespace CoreAI.ExampleGame.ArenaProgression.UseCases
{
    public interface IRollUpgradeOffersUseCase
    {
        bool Execute(HashSet<string> excludeIds, List<ArenaUpgradeOffer> into);
    }
}
