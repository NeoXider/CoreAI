namespace CoreAI.ExampleGame.ArenaProgression.Domain
{
    /// <summary>Снимок боевых чисел юнита (игрок / компаньон) на время забега.</summary>
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
