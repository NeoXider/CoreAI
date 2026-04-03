using System;
using CoreAI.ExampleGame.ArenaWaves.Domain;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaWaves.Infrastructure
{
    /// <summary>Линейный рост числа врагов (дефолт прототипа). Заменяемым на <see cref="IArenaWaveSchedule"/> из данных ИИ.</summary>
    [Serializable]
    public sealed class ArenaLinearWaveSchedule : IArenaWaveSchedule
    {
        [SerializeField] private int baseEnemyCount = 2;
        [SerializeField] private int extraPerWave = 2;

        public int GetEnemyCountForWave(int waveIndex1Based)
        {
            var w = waveIndex1Based < 1 ? 1 : waveIndex1Based;
            return baseEnemyCount + (w - 1) * extraPerWave;
        }
    }
}
