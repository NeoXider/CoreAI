using System;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaWaves.Domain
{
    [Serializable]
    public sealed class ArenaWavePlanEnvelope
    {
        public string commandType;
        public ArenaWavePlan payload;
    }

    /// <summary>Дескриптор волны, который может прийти от Creator (после валидации).</summary>
    [Serializable]
    public sealed class ArenaWavePlan
    {
        public int waveIndex1Based;

        public int enemyCount;

        /// <summary>Множитель hp врагов (1 = базовое значение в префабе/шаблоне).</summary>
        public float enemyHpMult = 1f;

        /// <summary>Множитель урона контакта врагов (1 = базовое значение).</summary>
        public float enemyDamageMult = 1f;

        /// <summary>Множитель скорости врагов (1 = базовое значение).</summary>
        public float enemyMoveSpeedMult = 1f;

        public float spawnIntervalSeconds = 0.45f;
        public float spawnRadius = 17.5f;
    }
}

