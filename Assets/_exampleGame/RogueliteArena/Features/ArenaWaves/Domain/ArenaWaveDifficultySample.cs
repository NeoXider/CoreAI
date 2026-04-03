using UnityEngine;

namespace CoreAI.ExampleGame.ArenaWaves.Domain
{
    /// <summary>Множители волны относительно базы (расписание / план Creator).</summary>
    public readonly struct ArenaWaveDifficultySample
    {
        public float EnemyCountMultiplier { get; }
        public float HpMultiplier { get; }
        public float DamageMultiplier { get; }
        public float MoveSpeedMultiplier { get; }
        /// <summary>Множитель к интервалу спавна (&lt;1 — чаще спавн).</summary>
        public float SpawnIntervalMultiplier { get; }

        public ArenaWaveDifficultySample(
            float enemyCountMult,
            float hpMult,
            float damageMult,
            float moveSpeedMult,
            float spawnIntervalMult)
        {
            EnemyCountMultiplier = enemyCountMult;
            HpMultiplier = hpMult;
            DamageMultiplier = damageMult;
            MoveSpeedMultiplier = moveSpeedMult;
            SpawnIntervalMultiplier = spawnIntervalMult;
        }

        public static ArenaWaveDifficultySample Identity { get; } = new(1f, 1f, 1f, 1f, 1f);
    }
}
