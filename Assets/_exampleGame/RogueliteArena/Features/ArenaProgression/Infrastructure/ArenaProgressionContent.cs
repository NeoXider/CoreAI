using System.Collections.Generic;
using Neo.Tools;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    [CreateAssetMenu(fileName = "ArenaProgressionContent", menuName = "CoreAI Example/Arena/Progression Content", order = 15)]
    public sealed class ArenaProgressionContent : ScriptableObject
    {
        [SerializeField] private ArenaRunBalanceConfig runBalance;
        [SerializeField] private ArenaPersistenceConfig persistence;
        [SerializeField] private ArenaUpgradePresentationConfig presentation;
        [SerializeField] private List<ArenaUpgradeDefinition> upgrades = new();
        [SerializeField] private ChanceData rarityRoll;
        [SerializeField] private ChanceData categoryCommonRare;
        [SerializeField] private ChanceData categoryEpic;
        [SerializeField] private ChanceData categoryLegendary;
        [SerializeField] private ChanceData statUpgradeWeights;

        public ArenaRunBalanceConfig RunBalance => runBalance;
        public ArenaPersistenceConfig Persistence => persistence;
        public ArenaUpgradePresentationConfig Presentation => presentation;
        public IReadOnlyList<ArenaUpgradeDefinition> Upgrades => upgrades;
        public ChanceData RarityRoll => rarityRoll;
        public ChanceData CategoryCommonRare => categoryCommonRare;
        public ChanceData CategoryEpic => categoryEpic;
        public ChanceData CategoryLegendary => categoryLegendary;
        public ChanceData StatUpgradeWeights => statUpgradeWeights;
    }
}
