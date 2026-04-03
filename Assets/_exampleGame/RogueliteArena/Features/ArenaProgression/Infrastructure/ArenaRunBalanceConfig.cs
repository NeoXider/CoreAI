using CoreAI.ExampleGame.ArenaProgression.Domain;
using Neo.Progression;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    [CreateAssetMenu(fileName = "ArenaRunBalance", menuName = "CoreAI Example/Arena/Run Balance Config", order = 11)]
    public sealed class ArenaRunBalanceConfig : ScriptableObject
    {
        [SerializeField] private LevelCurveDefinition sessionLevelCurve;
        [SerializeField] private LevelCurveDefinition metaLevelCurve;
        [SerializeField] private int baseXpPerKill = 10;
        [SerializeField] private bool divideXpByAliveTeamMembers = true;
        [SerializeField] private int startChoiceCount = 3;
        [SerializeField] private int maxChoiceCount = 5;
        [SerializeField] private float draftTimeScale = 0f;
        [SerializeField] private float commonStatMult = 1f;
        [SerializeField] private float rareStatMult = 1.5f;
        [SerializeField] private float epicStatMult = 2f;
        [SerializeField] private float legendaryStatMult = 3f;

        public LevelCurveDefinition SessionLevelCurve => sessionLevelCurve;
        public LevelCurveDefinition MetaLevelCurve => metaLevelCurve;
        public int BaseXpPerKill => baseXpPerKill;
        public bool DivideXpByAliveTeamMembers => divideXpByAliveTeamMembers;
        public int StartChoiceCount => startChoiceCount;
        public int MaxChoiceCount => maxChoiceCount;
        public float DraftTimeScale => draftTimeScale;

        public float GetStatMultiplier(ArenaRarity rarity) =>
            rarity switch
            {
                ArenaRarity.Common => commonStatMult,
                ArenaRarity.Rare => rareStatMult,
                ArenaRarity.Epic => epicStatMult,
                ArenaRarity.Legendary => legendaryStatMult,
                _ => 1f
            };
    }
}
