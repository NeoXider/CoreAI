using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Messaging;
using CoreAI.Session;
using UnityEngine;

namespace CoreAI.ExampleGame.Arena
{
    /// <summary>
    /// Получает ответы роли Creator (в виде конверта) и извлекает план волны (JSON).
    /// Директор запрашивает план через <see cref="RequestWavePlan"/>.
    /// </summary>
    public sealed class ArenaCreatorWavePlanner : MonoBehaviour
    {
        [SerializeField]
        private bool enabledInExample = true;

        [Tooltip("После стольких подряд невалидных ответов Creator переключаемся только на линейное расписание.")]
        [SerializeField]
        [Min(1)]
        private int maxInvalidPlansBeforeLinear = 3;

        private IAiOrchestrationService _orchestrator;
        private IArenaSessionView _session;
        private SessionTelemetryCollector _telemetry;
        private int _lastRequestedWave;
        private int _pendingCreatorWave;

        /// <summary>Планы по номеру волны (ответ LLM может прийти позже окна ожидания директора).</summary>
        private readonly Dictionary<int, ArenaWavePlan> _plansByWave = new Dictionary<int, ArenaWavePlan>();

        private int _invalidPlanStreak;
        private bool _forceLinear;

        /// <summary>Ждём валидный план от Creator для HUD «ИИ думает».</summary>
        public bool IsAwaitingCreatorPlan => _pendingCreatorWave > 0;

        /// <summary>После серии битых ответов директор не запрашивает Creator.</summary>
        public bool ForceLinearWavePlans => _forceLinear;

        public void Init(
            IAiOrchestrationService orchestrator,
            IArenaSessionView session,
            SessionTelemetryCollector telemetry = null)
        {
            _orchestrator = orchestrator;
            _session = session;
            _telemetry = telemetry;
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
            if (!enabledInExample || _forceLinear)
                return;
            if (_orchestrator == null || _session == null)
                return;

            _lastRequestedWave = waveIndex1Based;
            _pendingCreatorWave = waveIndex1Based;

            _telemetry?.SetTelemetry("arena.creator.request_wave", waveIndex1Based);
            _telemetry?.SetTelemetry("arena.creator.hint", $"arena_wave_plan wave={waveIndex1Based}");

            _ = _orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = $"arena_wave_plan wave={waveIndex1Based}",
                CancellationScope = "arena_creator_wave",
                Priority = waveIndex1Based
            });
        }

        public bool TryConsumeLatestPlan(int waveIndex1Based, out ArenaWavePlan plan)
        {
            if (_plansByWave.Remove(waveIndex1Based, out plan))
            {
                _pendingCreatorWave = 0;
                return true;
            }

            plan = null;
            return false;
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
            {
                RegisterInvalidPlan("parse_failed");
                return;
            }

            var waveKey = plan.waveIndex1Based > 0 ? plan.waveIndex1Based : _lastRequestedWave;
            if (!ArenaWavePlanValidator.TryValidate(plan, waveKey, out var fail))
            {
                Debug.LogWarning($"[CoreAI.ExampleGame] ArenaCreatorWavePlanner: план волны {waveKey} отклонён: {fail}");
                RegisterInvalidPlan(fail ?? "validate_failed");
                return;
            }

            _invalidPlanStreak = 0;
            _pendingCreatorWave = 0;
            _plansByWave[waveKey] = plan;
        }

        private void RegisterInvalidPlan(string reason)
        {
            _invalidPlanStreak++;
            _telemetry?.SetTelemetry("arena.creator.last_invalid_reason", reason ?? "");
            if (_invalidPlanStreak >= maxInvalidPlansBeforeLinear)
            {
                _forceLinear = true;
                _pendingCreatorWave = 0;
                Debug.LogWarning(
                    $"[CoreAI.ExampleGame] ArenaCreatorWavePlanner: достигнут лимит невалидных планов ({maxInvalidPlansBeforeLinear}), далее только линейное расписание.");
            }
        }
    }
}
