using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    [CreateAssetMenu(fileName = "ArenaUnitBaseline", menuName = "CoreAI Example/Arena/Unit Baseline Config", order = 10)]
    public sealed class ArenaUnitBaselineConfig : ScriptableObject
    {
        [SerializeField] private float playerMaxHealth = 100f;
        [SerializeField] private float playerHpRegenPerSecond;
        [SerializeField] private float playerMeleeDamage = 28f;
        [SerializeField] private float playerAttackCooldown = 0.45f;

        [SerializeField] private float companionMaxHealth = 80f;
        [SerializeField] private float companionHpRegenPerSecond;
        [SerializeField] private float companionMeleeDamage = 18f;
        [SerializeField] private float companionAttackCooldown = 0.55f;

        public float PlayerMaxHealth => playerMaxHealth;
        public float PlayerHpRegenPerSecond => playerHpRegenPerSecond;
        public float PlayerMeleeDamage => playerMeleeDamage;
        public float PlayerAttackCooldown => playerAttackCooldown;

        public float CompanionMaxHealth => companionMaxHealth;
        public float CompanionHpRegenPerSecond => companionHpRegenPerSecond;
        public float CompanionMeleeDamage => companionMeleeDamage;
        public float CompanionAttackCooldown => companionAttackCooldown;
    }
}
