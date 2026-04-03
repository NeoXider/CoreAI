using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.ExampleGame.ArenaAi.Domain;
using CoreAI.ExampleGame.ArenaBootstrap.Infrastructure;
using CoreAI.ExampleGame.ArenaSurvival.Infrastructure;
using CoreAI.ExampleGame.ArenaWaves.Infrastructure;
using CoreAI.Session;
using UnityEngine;
using VContainer;

namespace CoreAI.ExampleGame.ArenaAi.Infrastructure
{
    /// <summary>
    /// Единая шина «событие → <see cref="AiTaskRequest"/>» для арены: волна, HP, босс, комната, хоткеи (F1/F2).
    /// Размещается на корне сгенерированной арены; <see cref="Init"/> вызывает <see cref="ArenaSurvivalProceduralSetup"/>.
    /// </summary>
    public sealed class ArenaAiTaskBus : MonoBehaviour
    {
        [Header("События сессии")]
        [SerializeField]
        private bool reactToWaveChanged = true;

        [SerializeField]
        private bool reactToLowPlayerHp = true;

        [Tooltip("Доля текущего HP; ниже — одна задача AINpc на «кризис» (до восстановления выше hysteresis).")]
        [Range(0.05f, 0.9f)]
        [SerializeField]
        private float lowHpRatio = 0.28f;

        [Tooltip("Снять блок кризиса, когда HP выше этой доли.")]
        [Range(0.1f, 0.95f)]
        [SerializeField]
        private float lowHpHysteresisRatio = 0.38f;

        [SerializeField]
        private bool reactToBossDefeated = true;

        private CoreAILifetimeScope _scope;
        private ArenaSurvivalSession _session;
        private SessionTelemetryCollector _telemetry;
        private ArenaCreatorWavePlanner _planner;
        private bool _hpCrisisLatch;

        /// <summary>Явная инициализация из процедурного сетапа (предпочтительно).</summary>
        public void Init(
            CoreAILifetimeScope scope,
            ArenaSurvivalSession session,
            SessionTelemetryCollector telemetry,
            ArenaCreatorWavePlanner planner)
        {
            _scope = scope;
            _session = session;
            _telemetry = telemetry;
            _planner = planner;
            Subscribe();
        }

        private void Start()
        {
            if (_session == null)
                TryLazyBind();
            Subscribe();
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        private void TryLazyBind()
        {
            _scope ??= Object.FindAnyObjectByType<CoreAILifetimeScope>();
            _session ??= Object.FindAnyObjectByType<ArenaSurvivalSession>();
            _planner ??= Object.FindAnyObjectByType<ArenaCreatorWavePlanner>();
            if (_scope != null && _telemetry == null &&
                _scope.Container.TryResolve<ISessionTelemetryProvider>(out var tp) &&
                tp is SessionTelemetryCollector c)
                _telemetry = c;
        }

        private void Subscribe()
        {
            if (_session == null)
                return;
            _session.CurrentWaveChanged -= OnWaveChanged;
            _session.BossDefeated -= OnBossDefeated;
            if (_session.PrimaryPlayerHealth != null)
                _session.PrimaryPlayerHealth.Changed -= OnPlayerHpChanged;
            if (reactToWaveChanged)
                _session.CurrentWaveChanged += OnWaveChanged;
            if (reactToBossDefeated)
                _session.BossDefeated += OnBossDefeated;
            if (reactToLowPlayerHp && _session.PrimaryPlayerHealth != null)
                _session.PrimaryPlayerHealth.Changed += OnPlayerHpChanged;
        }

        private void Unsubscribe()
        {
            if (_session == null)
                return;
            _session.CurrentWaveChanged -= OnWaveChanged;
            _session.BossDefeated -= OnBossDefeated;
            if (_session.PrimaryPlayerHealth != null)
                _session.PrimaryPlayerHealth.Changed -= OnPlayerHpChanged;
        }

        private void OnWaveChanged(int wave)
        {
            _telemetry?.SetTelemetry("arena.event.wave_changed", wave);
            _telemetry?.SetTelemetry("arena.ai.last_event", $"{ArenaAiSourceTags.WaveChangedEvent}:{wave}");
            Debug.Log($"[CoreAI.ExampleGame] ArenaAiTaskBus: {ArenaAiSourceTags.WaveChangedEvent} wave={wave}");
        }

        private void OnBossDefeated()
        {
            _telemetry?.SetTelemetry("arena.ai.last_event", ArenaAiSourceTags.BossDefeated);
            if (!TryResolveOrchestrator(out var orch))
                return;
            var wave = _session != null ? _session.CurrentWave : 0;
            _ = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Analyzer,
                Hint =
                    $"arena_boss_defeated wave={wave}. Short commentary only (plain text): how might the next encounter escalate? No JSON.",
                SourceTag = ArenaAiSourceTags.BossDefeated,
                Priority = -20,
                CancellationScope = "arena_boss_defeated"
            });
        }

        private void OnPlayerHpChanged(int current, int max)
        {
            if (!reactToLowPlayerHp || max <= 0)
                return;
            var ratio = (float)current / max;
            if (!_hpCrisisLatch && ratio <= lowHpRatio)
            {
                _hpCrisisLatch = true;
                _telemetry?.SetTelemetry("arena.ai.last_event", ArenaAiSourceTags.PlayerHpCritical);
                if (!TryResolveOrchestrator(out var orch))
                    return;
                var wave = _session != null ? _session.CurrentWave : 0;
                var alive = _session != null ? _session.AliveEnemies : -1;
                _ = orch.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.AiNpc,
                    Hint =
                        $"{CoreAiArenaLlmHotkeys.CompanionHotkeyHintPrefix} arena_player_critical_hp wave={wave} alive_enemies={alive}. " +
                        "JSON only: stance + short battle_cry (schema as in companion F2 hint).",
                    SourceTag = ArenaAiSourceTags.PlayerHpCritical,
                    Priority = 40,
                    CancellationScope = "arena_player_hp_crisis"
                });
            }
            else if (_hpCrisisLatch && ratio >= lowHpHysteresisRatio)
                _hpCrisisLatch = false;
        }

        /// <summary>Триггер коллайдера «вход в комнату».</summary>
        public void NotifyRoomEntered(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId))
                roomId = "unnamed";
            var tag = ArenaAiSourceTags.RoomEnteredPrefix + roomId.Trim();
            _telemetry?.SetTelemetry("arena.ai.last_event", tag);
            if (!TryResolveOrchestrator(out var orch))
                return;
            _ = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.AiNpc,
                Hint = $"arena_room_entered room_id={roomId}. One short in-character line (plain text), no JSON.",
                SourceTag = tag,
                Priority = -5,
                CancellationScope = "arena_room_" + roomId.GetHashCode()
            });
        }

        /// <summary>Демо-хоткей F1 — тот же путь, что раньше в <see cref="CoreAiArenaLlmHotkeys"/>.</summary>
        public void FireHotkeyCreatorWavePlan()
        {
            if (_scope == null)
                TryLazyBind();
            if (_planner == null)
                _planner = Object.FindAnyObjectByType<ArenaCreatorWavePlanner>();

            var session = _session ?? Object.FindAnyObjectByType<ArenaSurvivalSession>();
            var wave = session != null ? Mathf.Max(1, session.CurrentWave) : 1;

            if (_planner != null && !_planner.ForceLinearWavePlans)
            {
                _planner.RequestWavePlan(wave, ArenaAiSourceTags.HotkeyF1);
                Debug.Log(
                    $"[CoreAI.ExampleGame] {ArenaAiSourceTags.HotkeyF1} → Creator: запрос плана волны {wave}.");
                return;
            }

            if (!TryResolveOrchestrator(out var orch))
                return;
            _ = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint =
                    $"manual_hotkey_F1 arena_creator_adhoc wave={wave}. " +
                    "Output compact JSON only, no markdown. " +
                    "Use exactly: {\"commandType\":\"ArenaWavePlan\",\"payload\":{" +
                    $"\"waveIndex1Based\":{wave},\"enemyCount\":4,\"enemyHpMult\":1,\"enemyDamageMult\":1," +
                    "\"enemyMoveSpeedMult\":1,\"spawnIntervalSeconds\":0.45,\"spawnRadius\":17.5}}. " +
                    "You may change enemyCount and multipliers; waveIndex1Based must match the wave above.",
                SourceTag = ArenaAiSourceTags.HotkeyF1,
                CancellationScope = "arena_hotkey_f1",
                Priority = 100
            });
            Debug.Log($"[CoreAI.ExampleGame] {ArenaAiSourceTags.HotkeyF1} → Creator: ad-hoc, wave={wave}.");
        }

        /// <summary>Демо-хоткей F2 — стойка компаньона.</summary>
        public void FireHotkeyCompanionNpc()
        {
            TryLazyBind();
            if (!TryResolveOrchestrator(out var orch))
                return;
            var session = _session ?? Object.FindAnyObjectByType<ArenaSurvivalSession>();
            var alive = session != null ? session.AliveEnemies : -1;
            var wave = session != null ? session.CurrentWave : 0;

            _ = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.AiNpc,
                Hint = CoreAiArenaLlmHotkeys.CompanionHotkeyHintPrefix +
                       " Pick companion combat demeanor. Output JSON only, no markdown fences. " +
                       "Schema: {\"stance\":\"aggressive\"|\"defensive\"|\"balanced\",\"battle_cry\":\"one short phrase\"}. " +
                       "aggressive: chase enemies from farther away, fight more eagerly. " +
                       "defensive: stay closer to the player, engage only nearby threats. " +
                       "balanced: middle ground. " +
                       $"Context: wave={wave}, alive_enemies={alive}.",
                SourceTag = ArenaAiSourceTags.HotkeyF2,
                CancellationScope = "arena_hotkey_f2_companion",
                Priority = 50
            });
            Debug.Log($"[CoreAI.ExampleGame] {ArenaAiSourceTags.HotkeyF2} → AINpc.");
        }

        private bool TryResolveOrchestrator(out IAiOrchestrationService orch)
        {
            orch = null;
            if (_scope == null)
                _scope = Object.FindAnyObjectByType<CoreAILifetimeScope>();
            if (_scope == null)
                return false;
            return _scope.Container.TryResolve(out orch);
        }
    }
}
