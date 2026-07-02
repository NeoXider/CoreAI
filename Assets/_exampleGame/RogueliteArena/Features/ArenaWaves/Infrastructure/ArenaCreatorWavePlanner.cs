using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.ExampleGame.ArenaAi.Domain;
using CoreAI.ExampleGame.ArenaSurvival.Domain;
using CoreAI.ExampleGame.ArenaWaves.Domain;
using CoreAI.ExampleGame.ArenaWaves.UseCases;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Messaging;
using CoreAI.Session;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaWaves.Infrastructure
{
    /// <summary>
    /// Receives Creator-role envelopes and extracts JSON wave plans requested by the director.
    /// </summary>
    public sealed class ArenaCreatorWavePlanner : MonoBehaviour
    {
        [SerializeField] private bool enabledInExample = true;

        [Tooltip("After this many consecutive invalid Creator responses, switch to the linear schedule only.")]
        [SerializeField]
        [Min(1)]
        private int maxInvalidPlansBeforeLinear = 3;

        private IAiOrchestrationService _orchestrator;
        private IArenaSessionView _session;
        private SessionTelemetryCollector _telemetry;
        private int _lastRequestedWave;
        private int _pendingCreatorWave;

        /// <summary>Plans keyed by wave number; LLM responses may arrive after the director wait window.</summary>
        private readonly Dictionary<int, ArenaWavePlan> _plansByWave = new();

        private int _invalidPlanStreak;
        private bool _forceLinear;

        /// <summary>True while the HUD should show that the AI is thinking about a valid Creator plan.</summary>
        public bool IsAwaitingCreatorPlan => _pendingCreatorWave > 0;

        /// <summary>True after repeated invalid responses disable further Creator planning requests.</summary>
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

        /// <param name="sourceTag">Source tag forwarded to telemetry and the user prompt; see <see cref="ArenaAiSourceTags"/>.</param>
        public void RequestWavePlan(int waveIndex1Based, string sourceTag = null)
        {
            if (!enabledInExample || _forceLinear)
            {
                return;
            }

            if (_orchestrator == null || _session == null)
            {
                return;
            }

            _lastRequestedWave = waveIndex1Based;
            _pendingCreatorWave = waveIndex1Based;

            string tag = string.IsNullOrWhiteSpace(sourceTag) ? ArenaAiSourceTags.DirectorWaveStart : sourceTag.Trim();
            _telemetry?.SetTelemetry("arena.creator.request_wave", waveIndex1Based);
            _telemetry?.SetTelemetry("arena.creator.hint", $"arena_wave_plan wave={waveIndex1Based}");
            _telemetry?.SetTelemetry("arena.ai.source", tag);

            string hint =
                $"arena_wave_plan wave={waveIndex1Based} context_version=1 " +
                "Telemetry keys: arena.context.version, arena.wave, arena.alive_enemies, arena.kills_this_wave, " +
                "player.hp.*, arena.wave_schedule.linear_enemy_count, arena.next_wave_index. " +
                "Contract: _exampleGame/Docs/CREATOR_WAVE_CONTEXT.md";

            _ = _orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = hint,
                SourceTag = tag,
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

        /// <summary>
        /// The director gave up waiting for this wave's plan and moved on with the fallback
        /// schedule. Clears the pending flag so the HUD's "AI thinking" indicator does not stay
        /// on after gameplay has already continued; a late-arriving valid plan is still stored
        /// for a future wave via <see cref="OnCommand"/>.
        /// </summary>
        public void NotifyPlanWaitAbandoned(int waveIndex1Based)
        {
            if (_pendingCreatorWave == waveIndex1Based)
            {
                _pendingCreatorWave = 0;
            }
        }

        private void OnCommand(ApplyAiGameCommand cmd)
        {
            if (!enabledInExample || cmd == null)
            {
                return;
            }

            if (cmd.CommandTypeId != AiGameCommandTypeIds.Envelope)
            {
                return;
            }

            if (!string.Equals(cmd.SourceRoleId, BuiltInAgentRoleIds.Creator, StringComparison.Ordinal))
            {
                return;
            }

            if (!ArenaWavePlanParser.TryParse(cmd.JsonPayload, out ArenaWavePlan plan))
            {
                RegisterInvalidPlan("parse_failed");
                return;
            }

            int waveKey = plan.waveIndex1Based > 0 ? plan.waveIndex1Based : _lastRequestedWave;
            if (!ArenaWavePlanValidator.TryValidate(plan, waveKey, out string fail))
            {
                Debug.LogWarning(
                    $"[CoreAI.ExampleGame] ArenaCreatorWavePlanner: wave {waveKey} plan rejected: {fail}");
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
                    $"[CoreAI.ExampleGame] ArenaCreatorWavePlanner: invalid-plan limit reached ({maxInvalidPlansBeforeLinear}); using the linear schedule from now on.");
            }
        }
    }
}