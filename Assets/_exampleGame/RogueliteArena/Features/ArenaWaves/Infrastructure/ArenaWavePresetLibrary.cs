using System.Collections.Generic;
using CoreAI.ExampleGame.ArenaWaves.Domain;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaWaves.Infrastructure
{
    /// <summary>Reference wave plans used for debugging and comparison; data only, no runtime logic.</summary>
    [CreateAssetMenu(menuName = "CoreAI Example/Arena Wave Preset Library", fileName = "ArenaWavePresetLibrary")]
    public sealed class ArenaWavePresetLibrary : ScriptableObject
    {
        [SerializeField]
        private List<ArenaWavePlan> presets = new();

        public IReadOnlyList<ArenaWavePlan> Presets => presets;
    }
}
