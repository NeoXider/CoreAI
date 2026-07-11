using System;
using CoreAI.ExampleGame.ArenaWaves.Domain;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaWaves.Infrastructure
{
    /// <summary>Prototype default schedule with linear enemy-count growth; replaceable by AI-validated data.</summary>
    [Serializable]
    public sealed class ArenaLinearWaveSchedule : IArenaWaveSchedule
    {
        [SerializeField]
        private int baseEnemyCount = 2;

        [SerializeField]
        private int extraPerWave = 2;

        public int GetEnemyCountForWave(int waveIndex1Based)
        {
            int w = waveIndex1Based < 1 ? 1 : waveIndex1Based;
            return baseEnemyCount + (w - 1) * extraPerWave;
        }
    }
}
