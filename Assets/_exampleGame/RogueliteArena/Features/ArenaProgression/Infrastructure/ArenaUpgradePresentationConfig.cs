using CoreAI.ExampleGame.ArenaProgression.Domain;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    [CreateAssetMenu(fileName = "ArenaUpgradePresentation", menuName = "CoreAI Example/Arena/Upgrade Presentation", order = 21)]
    public sealed class ArenaUpgradePresentationConfig : ScriptableObject
    {
        [SerializeField] private Sprite frameCommon;
        [SerializeField] private Sprite frameRare;
        [SerializeField] private Sprite frameEpic;
        [SerializeField] private Sprite frameLegendary;
        [SerializeField] private Material materialCommon;
        [SerializeField] private Material materialRare;
        [SerializeField] private Material materialEpic;
        [SerializeField] private Material materialLegendary;

        public Sprite GetFrame(ArenaRarity r) =>
            r switch
            {
                ArenaRarity.Common => frameCommon,
                ArenaRarity.Rare => frameRare,
                ArenaRarity.Epic => frameEpic,
                ArenaRarity.Legendary => frameLegendary,
                _ => frameCommon
            };

        public Material GetMaterial(ArenaRarity r) =>
            r switch
            {
                ArenaRarity.Common => materialCommon,
                ArenaRarity.Rare => materialRare,
                ArenaRarity.Epic => materialEpic,
                ArenaRarity.Legendary => materialLegendary,
                _ => materialCommon
            };
    }
}
