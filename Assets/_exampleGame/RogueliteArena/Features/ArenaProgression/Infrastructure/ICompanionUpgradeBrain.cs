using System.Collections.Generic;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    public interface ICompanionUpgradeBrain
    {
        ArenaUpgradeOffer Pick(IReadOnlyList<ArenaUpgradeOffer> candidates);
    }
}
