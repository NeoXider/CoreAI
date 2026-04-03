using CoreAI.ExampleGame.ArenaCombat.Infrastructure;
using CoreAI.ExampleGame.ArenaProgression.Domain;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    /// <summary>Мутация сессионных статов игрока/компаньона и синхронизация с <see cref="MonoBehaviour"/> на сцене.</summary>
    public sealed class ArenaRunCombatModel
    {
        private sealed class MutableStats : IArenaCombatStats
        {
            public float MaxHealth { get; set; }
            public float HpRegenPerSecond { get; set; }
            public float MeleeDamage { get; set; }
            public float AttackCooldownSeconds { get; set; }
        }

        private readonly MutableStats _player = new();
        private readonly MutableStats _companion = new();
        private readonly ArenaPlayerHealth _playerHealth;
        private readonly ArenaPlayerMelee _playerMelee;
        private readonly ArenaCompanionBot _companionBot;

        public ArenaRunCombatModel(
            ArenaUnitBaselineConfig baseline,
            ArenaPlayerHealth playerHealth,
            ArenaPlayerMelee playerMelee,
            ArenaCompanionBot companionBot)
        {
            _playerHealth = playerHealth;
            _playerMelee = playerMelee;
            _companionBot = companionBot;
            if (baseline != null)
            {
                var p = ArenaUnitRuntimeStats.FromBaseline(
                    baseline.PlayerMaxHealth,
                    baseline.PlayerHpRegenPerSecond,
                    baseline.PlayerMeleeDamage,
                    baseline.PlayerAttackCooldown);
                CopyFrom(ref p, _player);
                var c = ArenaUnitRuntimeStats.FromBaseline(
                    baseline.CompanionMaxHealth,
                    baseline.CompanionHpRegenPerSecond,
                    baseline.CompanionMeleeDamage,
                    baseline.CompanionAttackCooldown);
                CopyFrom(ref c, _companion);
            }

            PushPlayer();
            PushCompanion();
        }

        private static void CopyFrom(ref ArenaUnitRuntimeStats s, MutableStats m)
        {
            m.MaxHealth = s.MaxHealth;
            m.HpRegenPerSecond = s.HpRegenPerSecond;
            m.MeleeDamage = s.MeleeDamage;
            m.AttackCooldownSeconds = s.AttackCooldownSeconds;
        }

        public void ApplyUpgradeToPlayer(ArenaUpgradeDefinition def, float statMultiplier)
        {
            if (def == null)
                return;
            ApplyKind(def, statMultiplier, _player);
            PushPlayer();
        }

        public void ApplyUpgradeToCompanion(ArenaUpgradeDefinition def, float statMultiplier)
        {
            if (def == null)
                return;
            ApplyKind(def, statMultiplier, _companion);
            PushCompanion();
        }

        private static void ApplyKind(ArenaUpgradeDefinition def, float m, MutableStats s)
        {
            float d = def.StatDelta * m;
            switch (def.Kind)
            {
                case ArenaUpgradeKind.StatHp:
                    s.MaxHealth = Mathf.Max(1f, s.MaxHealth + d);
                    break;
                case ArenaUpgradeKind.StatHpRegen:
                    s.HpRegenPerSecond = Mathf.Max(0f, s.HpRegenPerSecond + d);
                    break;
                case ArenaUpgradeKind.StatDamage:
                    s.MeleeDamage = Mathf.Max(1f, s.MeleeDamage + d);
                    break;
                case ArenaUpgradeKind.StatAttackSpeed:
                    s.AttackCooldownSeconds = Mathf.Max(0.05f, s.AttackCooldownSeconds * (1f - 0.06f * def.StatDelta * m));
                    break;
            }
        }

        private void PushPlayer()
        {
            _playerHealth?.ApplyFromCombatStats(_player);
            _playerMelee?.ApplyFromCombatStats(_player);
        }

        private void PushCompanion()
        {
            _companionBot?.ApplyFromCombatStats(_companion);
        }
    }
}
