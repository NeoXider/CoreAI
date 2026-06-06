namespace CoreAI.ExampleGame.ArenaProgression.Domain
{
    /// <summary>Runtime combat-stat snapshot for a player or companion during one run.</summary>
    public struct ArenaUnitRuntimeStats
    {
        public float MaxHealth;
        public float HpRegenPerSecond;
        public float MeleeDamage;
        public float AttackCooldownSeconds;

        public static ArenaUnitRuntimeStats FromBaseline(float maxHp, float regen, float damage, float cooldown)
        {
            return new ArenaUnitRuntimeStats
            {
                MaxHealth = maxHp,
                HpRegenPerSecond = regen,
                MeleeDamage = damage,
                AttackCooldownSeconds = cooldown
            };
        }
    }
}