using System.Collections.Generic;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    /// <summary>Simple companion-upgrade heuristic based on upgrade kind and rarity bonus.</summary>
    public sealed class HeuristicCompanionUpgradeBrain : ICompanionUpgradeBrain
    {
        public ArenaUpgradeOffer Pick(IReadOnlyList<ArenaUpgradeOffer> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return null;
            ArenaUpgradeOffer best = null;
            var bestScore = int.MinValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c?.Definition == null)
                    continue;
                int s = c.HeuristicValueScore() + (int)c.RolledRarity * 5;
                if (s > bestScore)
                {
                    bestScore = s;
                    best = c;
                }
            }

            return best;
        }
    }
}
