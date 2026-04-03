using CoreAI.ExampleGame.ArenaAi.Infrastructure;
using CoreAI.ExampleGame.ArenaCamera.Infrastructure;
using CoreAI.ExampleGame.ArenaCombat.Infrastructure;
using CoreAI.ExampleGame.ArenaProgression.Infrastructure;
using CoreAI.ExampleGame.ArenaSurvival.Domain;
using CoreAI.ExampleGame.ArenaSurvival.UseCases;
using CoreAI.ExampleGame.ArenaSurvival.View;
using System;
using CoreAI.ExampleGame.ArenaWaves.Infrastructure;
using CoreAI.Session;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using VContainer;

namespace CoreAI.ExampleGame.ArenaSurvival.Infrastructure
{
    /// <summary>
    /// Собирает минимальную арену: сессия без синглтона, директор только на авторитетном узле.
    /// Мультиплеер: заменить спавн на сетевой (NGO), реплицировать волну/HP; роль <see cref="ArenaSimulationRole"/> на клиенте — <see cref="ArenaSimulationRole.ClientPresentationOnly"/>.
    /// </summary>
    public sealed class ArenaSurvivalProceduralSetup : MonoBehaviour
    {
        [SerializeField] private float arenaHalfSize = 22f;
        [SerializeField] private int wavesToWin = 10;
        [SerializeField] private ArenaSimulationRole simulationRole = ArenaSimulationRole.AuthoritativeHost;
        [SerializeField] private ArenaLinearWaveSchedule waveSchedule = new ArenaLinearWaveSchedule();

        [Tooltip("Если в сцене уже есть пол (Plane) с коллайдером — включите, чтобы не дублировать.")]
        [SerializeField]
        private bool skipRuntimeFloor;

        [Tooltip("Опционально: мировая позиция спавна игрока (пустой Transform на сцене).")]
        [SerializeField]
        private Transform playerSpawnAnchor;

        [Header("AI (Creator)")]
        [Tooltip("Если включено — на хосте запрашиваем у Creator план волны и применяем после валидации.")]
        [SerializeField]
        private bool creatorPlansWaves = true;

        [Header("Solo helper")]
        [Tooltip("Если включено — добавить NPC помощника в команду (соло).")]
        [SerializeField]
        private bool spawnCompanionBot = true;

        [Header("NavMesh")]
        [Tooltip("Автосоздание NavMeshSurface на полу и запекание (по умолчанию включено). Если данных нет — выполняется BuildNavMesh.")]
        [SerializeField]
        private bool buildNavMeshAtRuntime = true;

        [Tooltip("Перед запеканием временно отключить NavMeshAgent и CharacterController (игроки/боты), чтобы не ломать bake.")]
        [SerializeField]
        private bool suspendAgentsDuringNavMeshBake = true;

        [Tooltip("Всегда пересобирать NavMesh (дороже). Иначе — только если navMeshData пуст.")]
        [SerializeField]
        private bool forceFullNavMeshRebuild;

        [Header("Отладка")]
        [Tooltip("Один раз при сборке арены: какие роли LLM реально вызываются в примере.")]
        [SerializeField]
        private bool logOnStartRoles = true;

        [Tooltip("Опционально: пресеты волн для сравнения (read-only, не подключается автоматически).")]
        [SerializeField]
        private ArenaWavePresetLibrary wavePresetLibrary;

        [Header("Progression (VS-style)")]
        [Tooltip("Если заданы вместе с baseline — поднимается ArenaProgressionSessionHost, XP за килл, мета-сейв, Lua API.")]
        [SerializeField]
        private ArenaProgressionContent arenaProgressionContent;

        [SerializeField] private ArenaUnitBaselineConfig arenaUnitBaselineConfig;

        [Header("Волны — кривая сложности")]
        [Tooltip("Нелинейная кривая в духе Vampire Survivors (рост к концу + «лёгкие» волны). Пусто — без множителей.")]
        [SerializeField]
        private ArenaVsStyleWaveDifficulty vsWaveDifficulty;

        private void Start()
        {
            Build();
        }

        private void Build()
        {
            var root = new GameObject("ArenaGenerated");
            root.transform.SetParent(transform, false);

            NavMeshSurface navSurface = null;

            if (!skipRuntimeFloor)
            {
                var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                floor.name = "Floor";
                floor.transform.SetParent(root.transform, false);
                var scale = arenaHalfSize / 5f;
                floor.transform.localScale = new Vector3(scale, 1f, scale);
                ApplyLitColor(floor.GetComponent<Renderer>(), new Color(0.22f, 0.28f, 0.22f));
                if (buildNavMeshAtRuntime)
                {
                    navSurface = floor.GetComponent<NavMeshSurface>() ?? floor.AddComponent<NavMeshSurface>();
                    navSurface.collectObjects = CollectObjects.All;
                }
            }
            else if (buildNavMeshAtRuntime)
            {
                navSurface = UnityEngine.Object.FindAnyObjectByType<NavMeshSurface>(FindObjectsInactive.Include);
            }

            if (buildNavMeshAtRuntime && navSurface != null)
            {
                IDisposable suspend = null;
                if (suspendAgentsDuringNavMeshBake)
                    suspend = ArenaNavMeshRuntimeBake.SuspendAgentsForNavMeshBake(true);
                try
                {
                    ArenaNavMeshRuntimeBake.EnsureNavMeshBuilt(navSurface, forceFullNavMeshRebuild);
                }
                finally
                {
                    suspend?.Dispose();
                }
            }

            var sessionGo = new GameObject("ArenaSurvivalSession");
            sessionGo.transform.SetParent(root.transform, false);
            var session = sessionGo.AddComponent<ArenaSurvivalSession>();
            session.SetRuntimeSimulationRole(simulationRole);

            var player = CreatePlayer(root.transform);
            if (playerSpawnAnchor != null)
                player.transform.position = playerSpawnAnchor.position;
            var health = player.GetComponent<ArenaPlayerHealth>();
            session.RegisterPrimaryPlayer(player.transform, health);

            // Игра обновляет и хранит телеметрию в Core (SessionTelemetryCollector).
            var scopeForTelemetry = GetComponentInParent<CoreAI.Composition.CoreAILifetimeScope>();
            SessionTelemetryCollector telemetryCollector = null;
            if (scopeForTelemetry != null && scopeForTelemetry.Container.TryResolve<ISessionTelemetryProvider>(out var tp) &&
                tp is SessionTelemetryCollector sc)
            {
                telemetryCollector = sc;
                telemetryCollector.SetTelemetry("player.hp.current", health.Current);
                telemetryCollector.SetTelemetry("player.hp.max", health.Max);
                telemetryCollector.SetTelemetry("arena.alive_enemies", session.AliveEnemies);
            }

            health.Died += () =>
            {
                if (!session.RunEnded)
                    session.EndRun(false);
            };

            health.Changed += (cur, max) =>
            {
                telemetryCollector?.SetTelemetry("player.hp.current", cur);
                telemetryCollector?.SetTelemetry("player.hp.max", max);
            };

            session.AliveEnemiesChanged += alive =>
            {
                telemetryCollector?.SetTelemetry("arena.alive_enemies", alive);
            };

            var cam = Camera.main;
            if (cam != null)
            {
                var follow = cam.gameObject.GetComponent<ArenaFollowCamera>() ?? cam.gameObject.AddComponent<ArenaFollowCamera>();
                follow.SetTarget(player.transform);
            }

            var hudRoot = new GameObject("ArenaHUD");
            hudRoot.transform.SetParent(root.transform, false);
            var hud = hudRoot.AddComponent<ArenaSurvivalHud>();

            ArenaCompanionBot companionBot = null;
            if (spawnCompanionBot)
            {
                companionBot = CreateCompanionBot(root.transform);
                companionBot.transform.position = player.transform.position + new Vector3(1.5f, 0f, -1.5f);
                companionBot.Init(session);
            }

            var enemyTemplate = CreateEnemyTemplate();
            enemyTemplate.transform.SetParent(root.transform, false);

            ArenaCreatorWavePlanner planner = null;
            ArenaAuxLlmEveryNWaves auxLlm = null;
            CoreAI.Ai.IAiOrchestrationService orchestration = null;
            if (creatorPlansWaves)
            {
                var scope = scopeForTelemetry;
                if (scope != null)
                {
                    orchestration = scope.Container.Resolve<CoreAI.Ai.IAiOrchestrationService>();
                    planner = root.AddComponent<ArenaCreatorWavePlanner>();
                    planner.Init(orchestration, session, telemetryCollector);
                    auxLlm = root.AddComponent<ArenaAuxLlmEveryNWaves>();
                    auxLlm.Init(orchestration, session);
                }
            }

            var taskBus = root.AddComponent<ArenaAiTaskBus>();
            if (scopeForTelemetry != null)
                taskBus.Init(scopeForTelemetry, session, telemetryCollector, planner);

            ArenaProgressionSessionHost progressionHost = null;
            if (arenaProgressionContent != null && arenaUnitBaselineConfig != null)
            {
                var progGo = new GameObject("ArenaProgression");
                progGo.transform.SetParent(root.transform, false);
                progressionHost = progGo.AddComponent<ArenaProgressionSessionHost>();
                progressionHost.Configure(
                    arenaProgressionContent,
                    arenaUnitBaselineConfig,
                    spawnCompanionBot ? 2 : 1);
                progressionHost.Init(health, player.GetComponent<ArenaPlayerMelee>(), companionBot);
                progressionHost.Bootstrap();
                progGo.AddComponent<ArenaProgressionDebugHotkey>();
            }

            hud.Bind(session, health, planner, auxLlm, progressionHost?.Team, progressionHost?.SessionLevelCurve);

            var dirGo = new GameObject("ArenaSurvivalDirector");
            dirGo.transform.SetParent(root.transform, false);
            var director = dirGo.AddComponent<ArenaSurvivalDirector>();
            director.Init(session, enemyTemplate, waveSchedule, planner, wavesToWin, orchestration, vsWaveDifficulty);

            if (logOnStartRoles)
            {
                var presetInfo = wavePresetLibrary != null
                    ? $" Пресеты волн в ассете: {wavePresetLibrary.Presets.Count}."
                    : "";
                Debug.Log(
                    "[CoreAI.ExampleGame] ИИ: ArenaAiTaskBus (события волны/HP/босс/комната + демо F1/F2); Creator — план волны и предзапрос следующей; " +
                    "раз в N волн — Analyzer/AINpc (ArenaAuxLlmEveryNWaves); пост-волна — Analyzer (лог); Programmer — F9. " +
                    "Контекст Creator: Docs/CREATOR_WAVE_CONTEXT.md." +
                    presetInfo);
            }
        }

        private GameObject CreatePlayer(Transform parent)
        {
            var go = new GameObject("Player");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(0f, 0f, 0f);
            go.tag = "Player";

            var col = go.AddComponent<CapsuleCollider>();
            col.height = 2f;
            col.radius = 0.42f;
            col.center = new Vector3(0f, 1f, 0f);
            col.enabled = false;

            var cc = go.AddComponent<CharacterController>();
            cc.height = 2f;
            cc.radius = 0.42f;
            cc.center = new Vector3(0f, 1f, 0f);

            var vis = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            vis.name = "Vis";
            vis.transform.SetParent(go.transform, false);
            vis.transform.localPosition = new Vector3(0f, 1f, 0f);
            DestroyCollider(vis);
            ApplyLitColor(vis.GetComponent<Renderer>(), new Color(0.35f, 0.55f, 0.95f));

            go.AddComponent<ArenaPlayerMotor>();
            var hp = go.AddComponent<ArenaPlayerHealth>();
            go.AddComponent<ArenaPlayerMelee>();
            return go;
        }

        private GameObject CreateEnemyTemplate()
        {
            var go = new GameObject("EnemyTemplate");
            go.SetActive(false);
            var vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            vis.name = "Vis";
            vis.transform.SetParent(go.transform, false);
            vis.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            vis.transform.localScale = new Vector3(0.75f, 1.15f, 0.75f);
            DestroyCollider(vis);
            ApplyLitColor(vis.GetComponent<Renderer>(), new Color(0.75f, 0.22f, 0.18f));

            var box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.6f, 0f);
            box.size = new Vector3(0.8f, 1.2f, 0.8f);

            go.AddComponent<ArenaEnemyBrain>();
            var nav = go.AddComponent<NavMeshAgent>();
            nav.height = 1.2f;
            nav.radius = 0.35f;
            nav.baseOffset = 0.6f;
            return go;
        }

        private ArenaCompanionBot CreateCompanionBot(Transform parent)
        {
            var go = new GameObject("CompanionBot");
            go.transform.SetParent(parent, false);

            var cc = go.AddComponent<CharacterController>();
            cc.height = 2f;
            cc.radius = 0.42f;
            cc.center = new Vector3(0f, 1f, 0f);

            var vis = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            vis.name = "Vis";
            vis.transform.SetParent(go.transform, false);
            vis.transform.localPosition = new Vector3(0f, 1f, 0f);
            DestroyCollider(vis);
            ApplyLitColor(vis.GetComponent<Renderer>(), new Color(0.2f, 0.95f, 0.6f));

            var bot = go.AddComponent<ArenaCompanionBot>();
            go.AddComponent<ArenaCompanionAiListener>();
            return bot;
        }

        private static void DestroyCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c != null)
                Destroy(c);
        }

        private static void ApplyLitColor(Renderer r, Color c)
        {
            if (r == null)
                return;
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null)
                sh = Shader.Find("Standard");
            var mat = new Material(sh);
            if (sh.name.Contains("Universal") || sh.name.Contains("URP"))
                mat.SetColor("_BaseColor", c);
            else
                mat.color = c;
            r.sharedMaterial = mat;
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.rKey.wasPressedThisFrame)
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }
    }
}
