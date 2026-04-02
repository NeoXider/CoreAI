using System;
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
        private ArenaWavePlan _lastPlan;

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
            plan = null;
            if (_lastPlan == null)
                return false;
            if (_lastRequestedWave != waveIndex1Based)
                return false;

            plan = _lastPlan;
            _lastPlan = null;
            return true;
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

            if (!ArenaWavePlanValidator.TryValidate(plan, _lastRequestedWave, out _))
                return;

            _lastPlan = plan;
        }
    }
}

