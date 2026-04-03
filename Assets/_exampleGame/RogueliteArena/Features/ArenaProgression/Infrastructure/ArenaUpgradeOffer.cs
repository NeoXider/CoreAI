using CoreAI.ExampleGame.ArenaProgression.Domain;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    public sealed class ArenaUpgradeOffer
    {
        public ArenaUpgradeDefinition Definition { get; }
        public ArenaRarity RolledRarity { get; }
        public float StatMultiplier { get; }

        public ArenaUpgradeOffer(ArenaUpgradeDefinition definition, ArenaRarity rolledRarity, float statMultiplier)
        {
            Definition = definition;
            RolledRarity = rolledRarity;
            StatMultiplier = statMultiplier;
        }

        public int HeuristicValueScore()
        {
            if (Definition == null)
                return 0;
            var k = Definition.Kind;
            return k switch
            {
                ArenaUpgradeKind.StatDamage => 40,
                ArenaUpgradeKind.StatAttackSpeed => 35,
                ArenaUpgradeKind.StatHp => 25,
                ArenaUpgradeKind.StatHpRegen => 20,
                ArenaUpgradeKind.PassiveSlotPlusOne => 30,
                ArenaUpgradeKind.OfferExtraChoices => 28,
                ArenaUpgradeKind.LegendaryDoublePickThisWave => 45,
                _ => 10
            };
        }
    }
}
