using CoreAI.ExampleGame.ArenaProgression.Domain;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    [CreateAssetMenu(fileName = "ArenaUpgrade", menuName = "CoreAI Example/Arena/Upgrade Definition", order = 20)]
    public sealed class ArenaUpgradeDefinition : ScriptableObject
    {
        [SerializeField] private string id;
        [SerializeField] private string title;
        [SerializeField] [TextArea(2, 6)] private string description;
        [SerializeField] private Sprite icon;
        [SerializeField] private ArenaUpgradeKind kind;
        [SerializeField] private ArenaRarity rarity;
        [SerializeField] private float statDelta;

        public string Id => string.IsNullOrEmpty(id) ? name : id;
        public string Title => title;
        public string Description => description;
        public Sprite Icon => icon;
        public ArenaUpgradeKind Kind => kind;
        public ArenaRarity Rarity => rarity;
        public float StatDelta => statDelta;
    }
}
