using System.Collections;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.ExampleGame.ArenaAi.Domain;
using CoreAI.ExampleGame.ArenaCombat.Infrastructure;
using CoreAI.ExampleGame.ArenaSurvival.Domain;
using CoreAI.ExampleGame.ArenaWaves.Domain;
using CoreAI.ExampleGame.ArenaWaves.Infrastructure;
using CoreAI.ExampleGame.ArenaWaves.UseCases;
using CoreAI.Infrastructure.Llm;
using CoreAI.Session;
using UnityEngine;
using UnityEngine.AI;
using VContainer;
using VContainer.Unity;

namespace CoreAI.ExampleGame.ArenaSurvival.UseCases
{
    /// <summary>
    /// Волны и спавн — только при <see cref="IArenaSessionAuthority.IsAuthoritativeSimulation"/>.
    /// В мультиплеере компонент на сервере; на клиенте отключён или заменён воспроизведением сетевых spawn.
    /// </summary>
    public sealed class ArenaSurvivalDirector : MonoBehaviour
    {
        [SerializeField] private int wavesToWin = 10;
        [SerializeField] private float spawnInterval = 0.45f;
        [SerializeField] private float pauseBetweenWaves = 2f;
        [SerializeField] private float spawnRadius = 17.5f;

        [Tooltip("Сколько секунд ждать ответ Creator (LLM) перед запасным планом из линейного расписания.")]
        [SerializeField]
        private float creatorPlanWaitSeconds = 14f;

        [Header("Предзапрос плана следующей волны")]
        [Tooltip("Когда оставшихся врагов не больше этого числа — запросить план волны N+1 (один раз за волну).")]
        [SerializeField]
        [Min(0)]
        private int preRequestNextWaveWhenAliveAtMost = 2;

        [Header("Пост-волна Analyzer")]
        [SerializeField]
        private bool runPostWaveAnalyzer = true;

        [Header("Сложность волн (VS-style)")]
        [Tooltip("Нелинейная кривая: суммарно сложнее к концу рана, отдельные волны мягче/жёстче. Пусто — только план / линейное расписание.")]
        [SerializeField]
        private ArenaVsStyleWaveDifficulty waveDifficultyProfile;

        private IArenaSessionAuthority _session;
        private GameObject _enemyTemplate;
        private IArenaWaveSchedule _waveSchedule;
        private ArenaCreatorWavePlanner _creatorPlanner;
        private IAiOrchestrationService _aiOrchestration;
        private IArenaWaveDifficulty _waveDifficulty;
        private bool _useLocalCreator;
        private bool _started;

        public void Init(
            IArenaSessionAuthority session,
            GameObject enemyTemplate,
            IArenaWaveSchedule waveSchedule,
            ArenaCreatorWavePlanner creatorPlanner,
            int winWaves,
            IAiOrchestrationService aiOrchestration = null,
            IArenaWaveDifficulty waveDifficultyOverride = null)
        {
            _session = session;
            _enemyTemplate = enemyTemplate;
            _waveSchedule = waveSchedule;
            _creatorPlanner = creatorPlanner;
            wavesToWin = winWaves;
            _aiOrchestration = aiOrchestration;
            _waveDifficulty = waveDifficultyOverride != null ? waveDifficultyOverride : waveDifficultyProfile;

            // Если ILlmClient = StubLlmClient — используем локальный планировщик (пример),
            // чтобы Core оставался игронезависимым.
            var scope = LifetimeScope.Find<CoreAILifetimeScope>();
            if (scope != null && scope.Container.TryResolve<ILlmClient>(out var llm))
                _useLocalCreator = LoggingLlmClientDecorator.Unwrap(llm) is StubLlmClient;
        }

        private void Start()
        {
            if (_started || _session == null || _enemyTemplate == null || _waveSchedule == null)
                return;
            if (!_session.IsAuthoritativeSimulation)
                return;
            _started = true;
            StartCoroutine(RunWaves());
        }

        private IEnumerator RunWaves()
        {
            for (var wave = 1; wave <= wavesToWin; wave++)
            {
                if (_session.RunEnded)
                    yield break;

                _session.ResetKillsThisWave();
                var waveStartRt = Time.realtimeSinceStartup;
                PushWaveTelemetry(wave);
                _session.SetCurrentWave(wave);
                ArenaWavePlan plan = null;
                if (_useLocalCreator)
                {
                    plan = ArenaLocalWavePlanner.CreatePlan(wave, _session, _waveSchedule);
                }
                else if (_creatorPlanner != null && !_creatorPlanner.ForceLinearWavePlans)
                {
                    _creatorPlanner.RequestWavePlan(wave, ArenaAiSourceTags.DirectorWaveStart);
                    var t = 0f;
                    while (t < creatorPlanWaitSeconds && !_session.RunEnded)
                    {
                        if (_creatorPlanner.TryConsumeLatestPlan(wave, out plan))
                            break;
                        t += Time.unscaledDeltaTime;
                        yield return null;
                    }
                }

                var vs = _waveDifficulty != null
                    ? _waveDifficulty.Evaluate(wave, wavesToWin)
                    : ArenaWaveDifficultySample.Identity;

                var count = plan != null ? plan.enemyCount : _waveSchedule.GetEnemyCountForWave(wave);
                count = Mathf.Clamp(Mathf.RoundToInt(count * vs.EnemyCountMultiplier), 1, 500);

                var hpMult = Mathf.Clamp((plan != null ? plan.enemyHpMult : 1f) * vs.HpMultiplier, 0.25f, 5f);
                var dmgMult = Mathf.Clamp((plan != null ? plan.enemyDamageMult : 1f) * vs.DamageMultiplier, 0.25f, 5f);
                var msMult = Mathf.Clamp((plan != null ? plan.enemyMoveSpeedMult : 1f) * vs.MoveSpeedMultiplier, 0.25f, 3f);
                var interval = Mathf.Clamp(
                    (plan != null ? plan.spawnIntervalSeconds : spawnInterval) * vs.SpawnIntervalMultiplier,
                    0.05f,
                    3f);
                var radius = plan != null ? plan.spawnRadius : spawnRadius;
                for (var i = 0; i < count; i++)
                {
                    if (_session.RunEnded)
                        yield break;
                    SpawnOne(hpMult, dmgMult, msMult, radius);
                    yield return new WaitForSeconds(interval);
                }

                var preRequested = false;
                while (_session.AliveEnemies > 0 && !_session.RunEnded)
                {
                    if (!preRequested &&
                        !_useLocalCreator &&
                        _creatorPlanner != null &&
                        !_creatorPlanner.ForceLinearWavePlans &&
                        wave < wavesToWin &&
                        _session.AliveEnemies <= preRequestNextWaveWhenAliveAtMost)
                    {
                        _creatorPlanner.RequestWavePlan(wave + 1, ArenaAiSourceTags.DirectorPreNextWave);
                        preRequested = true;
                    }

                    yield return null;
                }

                if (_session.RunEnded)
                    yield break;
                PushLastWaveDurationTelemetry(Time.realtimeSinceStartup - waveStartRt);
                TryRunPostWaveAnalyzer(wave);
                yield return new WaitForSecondsRealtime(pauseBetweenWaves);
            }

            if (!_session.RunEnded)
                _session.EndRun(true);
        }

        private void TryRunPostWaveAnalyzer(int completedWave)
        {
            if (!runPostWaveAnalyzer || _useLocalCreator || _aiOrchestration == null)
                return;
            _ = _aiOrchestration.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Analyzer,
                Hint =
                    $"arena_post_wave_reflect completed_wave={completedWave}. " +
                    "One short paragraph only (plain text): was this wave likely too easy or too hard given telemetry? " +
                    "Diagnostic for designers; no JSON, no commands.",
                SourceTag = $"arena_post_wave:{completedWave}",
                Priority = -60,
                CancellationScope = "arena_post_wave_analyzer"
            });
        }

        private void PushWaveTelemetry(int wave)
        {
            var scope = LifetimeScope.Find<CoreAILifetimeScope>();
            if (scope == null)
                return;
            if (!scope.Container.TryResolve<ISessionTelemetryProvider>(out var tp))
                return;
            if (tp is not SessionTelemetryCollector collector)
                return;

            collector.SetTelemetry("arena.context.version", "1");
            collector.SetTelemetry("wave", wave);
            collector.SetTelemetry("arena.wave", wave);
            collector.SetTelemetry("arena.wave_schedule.linear_enemy_count", _waveSchedule.GetEnemyCountForWave(wave));
            if (_waveDifficulty != null)
            {
                var vs = _waveDifficulty.Evaluate(wave, wavesToWin);
                collector.SetTelemetry("arena.wave.vs.enemy_count_mult", vs.EnemyCountMultiplier);
                collector.SetTelemetry("arena.wave.vs.hp_mult", vs.HpMultiplier);
                collector.SetTelemetry("arena.wave.vs.dmg_mult", vs.DamageMultiplier);
                collector.SetTelemetry("arena.wave.vs.move_mult", vs.MoveSpeedMultiplier);
                collector.SetTelemetry("arena.wave.vs.spawn_interval_mult", vs.SpawnIntervalMultiplier);
            }
            if (wave < wavesToWin)
                collector.SetTelemetry("arena.next_wave_index", wave + 1);
            else
                collector.SetTelemetry("arena.next_wave_index", "");

            collector.SetTelemetry("arena.alive_enemies", _session.AliveEnemies);
            collector.SetTelemetry("arena.kills_this_wave", _session.KillsThisWave);
            collector.SetTelemetry("arena.total_kills_run", _session.TotalKillsRun);

            if (_session.PrimaryPlayerHealth != null)
            {
                var h = _session.PrimaryPlayerHealth;
                collector.SetTelemetry("player.hp.current", h.Current);
                collector.SetTelemetry("player.hp.max", h.Max);
                var pct = h.Max > 0 ? 100f * h.Current / h.Max : 0f;
                collector.SetTelemetry("player.hp.pct", pct);
            }
        }

        private void PushLastWaveDurationTelemetry(float seconds)
        {
            var scope = LifetimeScope.Find<CoreAILifetimeScope>();
            if (scope == null)
                return;
            if (!scope.Container.TryResolve<ISessionTelemetryProvider>(out var tp))
                return;
            if (tp is SessionTelemetryCollector collector)
                collector.SetTelemetry("arena.last_wave_duration_sec", seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        }

        private void SpawnOne(float hpMult, float dmgMult, float moveSpeedMult, float radius)
        {
            var angle = Random.Range(0f, Mathf.PI * 2f);
            var pos = new Vector3(Mathf.Cos(angle) * radius, 0.6f, Mathf.Sin(angle) * radius);
            var e = Instantiate(_enemyTemplate, pos, Quaternion.identity);
            var brain = e.GetComponent<ArenaEnemyBrain>();
            if (brain != null)
            {
                brain.Configure(_session);
                brain.ApplyWaveStats(hpMult, dmgMult, moveSpeedMult);
            }

            var agent = e.GetComponent<NavMeshAgent>();
            var enableNav = false;
            if (agent != null)
            {
                agent.enabled = false;
                if (NavMesh.SamplePosition(pos, out var hit, 12f, NavMesh.AllAreas))
                {
                    e.transform.position = hit.position;
                    enableNav = true;
                }
            }

            e.SetActive(true);
            if (agent != null && enableNav)
                agent.enabled = true;
        }
    }
}
