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

    /// <summary>Validated wave descriptor that can be produced by the Creator role.</summary>
    [Serializable]
    public sealed class ArenaWavePlan
    {
        public int waveIndex1Based;

        public int enemyCount;

        /// <summary>Enemy HP multiplier; <c>1</c> keeps the prefab or template baseline.</summary>
        public float enemyHpMult = 1f;

        /// <summary>Enemy contact-damage multiplier; <c>1</c> keeps the baseline value.</summary>
        public float enemyDamageMult = 1f;

        /// <summary>Enemy movement-speed multiplier; <c>1</c> keeps the baseline value.</summary>
        public float enemyMoveSpeedMult = 1f;

        public float spawnIntervalSeconds = 0.45f;
        public float spawnRadius = 17.5f;
    }
}

