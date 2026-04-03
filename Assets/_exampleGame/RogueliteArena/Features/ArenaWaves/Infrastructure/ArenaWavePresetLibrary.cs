using System.Collections.Generic;
using CoreAI.ExampleGame.ArenaWaves.Domain;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaWaves.Infrastructure
{
    /// <summary>Эталонные планы волн для отладки / сравнения (только данные, без рантайм-логики).</summary>
    [CreateAssetMenu(menuName = "CoreAI Example/Arena Wave Preset Library", fileName = "ArenaWavePresetLibrary")]
    public sealed class ArenaWavePresetLibrary : ScriptableObject
    {
        [SerializeField]
        private List<ArenaWavePlan> presets = new List<ArenaWavePlan>();

        public IReadOnlyList<ArenaWavePlan> Presets => presets;
    }
}
