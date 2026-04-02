using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    /// <summary>
    /// Раз в N волн запускает лёгкие задачи Analyzer и AINpc (для демонстрации маршрутизации ролей).
    /// </summary>
    public sealed class ArenaAuxLlmEveryNWaves : MonoBehaviour
    {
        [SerializeField]
        [Min(1)]
        private int everyNWaves = 3;

        private IAiOrchestrationService _orchestrator;
        private ArenaSurvivalSession _session;
        private int _lastWaveTriggered;

        /// <summary>Краткая строка для HUD после триггера вспомогательных ролей.</summary>
        public string StatusLine { get; private set; }

        public void Init(IAiOrchestrationService orchestrator, ArenaSurvivalSession session)
        {
            _orchestrator = orchestrator;
            _session = session;
            if (_session != null)
                _session.CurrentWaveChanged += OnWave;
        }

        private void OnDestroy()
        {
            if (_session != null)
                _session.CurrentWaveChanged -= OnWave;
        }

        private void OnWave(int wave)
        {
            if (_orchestrator == null || wave <= 0 || everyNWaves <= 0)
                return;
            if (wave % everyNWaves != 0)
                return;
            if (wave == _lastWaveTriggered)
                return;
            _lastWaveTriggered = wave;
            StatusLine = $"Волна {wave}: запросы Analyzer + AINpc к LLM";
            _ = _orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Analyzer,
                Hint = $"arena_aux_wave={wave}",
                Priority = -1
            });
            _ = _orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.AiNpc,
                Hint = $"arena_aux_wave={wave}",
                Priority = -2
            });
        }
    }
}
