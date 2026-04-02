using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Messaging;
using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    /// <summary>
    /// Получает ответы роли Creator (в виде конверта) и извлекает план волны (JSON).
    /// Директор запрашивает план через <see cref="RequestWavePlan"/>.
    /// </summary>
    public sealed class ArenaCreatorWavePlanner : MonoBehaviour
    {
        [SerializeField] private bool enabledInExample = true;

        private IAiOrchestrationService _orchestrator;
        private IArenaSessionView _session;
        private int _lastRequestedWave;

        /// <summary>Планы по номеру волны (ответ LLM может прийти позже окна ожидания директора).</summary>
        private readonly Dictionary<int, ArenaWavePlan> _plansByWave = new Dictionary<int, ArenaWavePlan>();

        public void Init(IAiOrchestrationService orchestrator, IArenaSessionView session)
        {
            _orchestrator = orchestrator;
            _session = session;
        }

        private void OnEnable()
        {
            AiGameCommandRouter.CommandReceived += OnCommand;
        }

        private void OnDisable()
        {
            AiGameCommandRouter.CommandReceived -= OnCommand;
        }

        public void RequestWavePlan(int waveIndex1Based)
        {
            if (!enabledInExample)
                return;
            if (_orchestrator == null || _session == null)
                return;

            _lastRequestedWave = waveIndex1Based;

            _ = _orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "arena_wave_plan"
            });
        }

        public bool TryConsumeLatestPlan(int waveIndex1Based, out ArenaWavePlan plan)
        {
            return _plansByWave.Remove(waveIndex1Based, out plan);
        }

        private void OnCommand(ApplyAiGameCommand cmd)
        {
            if (!enabledInExample || cmd == null)
                return;
            if (cmd.CommandTypeId != AiGameCommandTypeIds.Envelope)
                return;
            if (!string.Equals(cmd.SourceRoleId, BuiltInAgentRoleIds.Creator, StringComparison.Ordinal))
                return;

            if (!ArenaWavePlanParser.TryParse(cmd.JsonPayload, out var plan))
                return;

            var waveKey = plan.waveIndex1Based > 0 ? plan.waveIndex1Based : _lastRequestedWave;
            if (!ArenaWavePlanValidator.TryValidate(plan, waveKey, out var fail))
            {
                Debug.LogWarning($"[CoreAI.ExampleGame] ArenaCreatorWavePlanner: план волны {waveKey} отклонён: {fail}");
                return;
            }

            _plansByWave[waveKey] = plan;
        }
    }
}
