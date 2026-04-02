using System.Collections;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Session;
using UnityEngine;
using UnityEngine.AI;
using VContainer;
using VContainer.Unity;

namespace CoreAI.ExampleGame.Arena
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

        private IArenaSessionAuthority _session;
        private GameObject _enemyTemplate;
        private IArenaWaveSchedule _waveSchedule;
        private ArenaCreatorWavePlanner _creatorPlanner;
        private bool _useLocalCreator;
        private bool _started;

        public void Init(
            IArenaSessionAuthority session,
            GameObject enemyTemplate,
            IArenaWaveSchedule waveSchedule,
            ArenaCreatorWavePlanner creatorPlanner,
            int winWaves)
        {
            _session = session;
            _enemyTemplate = enemyTemplate;
            _waveSchedule = waveSchedule;
            _creatorPlanner = creatorPlanner;
            wavesToWin = winWaves;

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
                    // Запрос Creator (асинхронно) — ждём ответ LLM до creatorPlanWaitSeconds, иначе запасной план.
                    _creatorPlanner.RequestWavePlan(wave);
                    var t = 0f;
                    while (t < creatorPlanWaitSeconds && !_session.RunEnded)
                    {
                        if (_creatorPlanner.TryConsumeLatestPlan(wave, out plan))
                            break;
                        t += Time.unscaledDeltaTime;
                        yield return null;
                    }
                }

                var count = plan != null ? plan.enemyCount : _waveSchedule.GetEnemyCountForWave(wave);
                var hpMult = plan != null ? plan.enemyHpMult : 1f;
                var dmgMult = plan != null ? plan.enemyDamageMult : 1f;
                var msMult = plan != null ? plan.enemyMoveSpeedMult : 1f;
                var interval = plan != null ? plan.spawnIntervalSeconds : spawnInterval;
                var radius = plan != null ? plan.spawnRadius : spawnRadius;
                for (var i = 0; i < count; i++)
                {
                    if (_session.RunEnded)
                        yield break;
                    SpawnOne(hpMult, dmgMult, msMult, radius);
                    yield return new WaitForSeconds(interval);
                }

                while (_session.AliveEnemies > 0 && !_session.RunEnded)
                    yield return null;
                if (_session.RunEnded)
                    yield break;
                PushLastWaveDurationTelemetry(Time.realtimeSinceStartup - waveStartRt);
                yield return new WaitForSecondsRealtime(pauseBetweenWaves);
            }

            if (!_session.RunEnded)
                _session.EndRun(true);
        }

        private void PushWaveTelemetry(int wave)
        {
            var scope = LifetimeScope.Find<CoreAILifetimeScope>();
            if (scope == null)
                return;
            if (!scope.Container.TryResolve<ISessionTelemetryProvider>(out var tp))
                return;
            if (tp is SessionTelemetryCollector collector)
            {
                collector.SetTelemetry("wave", wave);
                collector.SetTelemetry("arena.wave", wave);
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
