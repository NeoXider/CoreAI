using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Ai;
using CoreAI.Infrastructure.AiMemory;
using CoreAI.Infrastructure.Config;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Infrastructure.Prompts;
using CoreAI.Authority;
using CoreAI.Infrastructure.World;
using LLMUnity;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Корневой VContainer scope **ядра CoreAI** (лог, MessagePipe, промпты, LLM, оркестратор).
    /// Имя отделено от «игрового» <see cref="LifetimeScope"/>: на сцене игры добавляйте свои feature-scope'ы
    /// с <b>Parent</b> = этот объект, чтобы не путать с корнем тайтла.
    /// </summary>
    public sealed class CoreAILifetimeScope : LifetimeScope
    {
        [Tooltip(
            "Если null — логируются все фичи (DefaultGameLogSettings). Иначе — фильтр по флагам и минимальному уровню.")]
        [SerializeField]
        private GameLogSettingsAsset gameLogSettings;

        [Tooltip("Опционально: переопределения и кастомные агенты (системный/user шаблоны из TextAsset).")]
        [SerializeField]
        private AgentPromptsManifest agentPromptsManifest;

        [Tooltip(
            "Если Use OpenAi Compatible Http включён в asset — ILlmClient ходит в chat/completions (OpenAI-совместимо), иначе LLMAgent / заглушка.")]
        [SerializeField]
        private OpenAiHttpLlmSettings openAiHttpLlmSettings;

        [Tooltip(
            "Опционально: маршрутизация ILlmClient по роли (Enable Role Routing). Иначе — только legacy Open Ai + LLMUnity.")]
        [SerializeField]
        private LlmRoutingManifest llmRoutingManifest;

        [Tooltip("Автоотмена одного вызова ILlmClient.CompleteAsync, если модель «зависла». 0 — без ограничения.")]
        [SerializeField]
        private float llmRequestTimeoutSeconds = 15f;

        [Tooltip("Максимум параллельных задач IAiOrchestrationService (очередь остальных).")] [SerializeField] [Min(1)]
        private int aiOrchestrationMaxConcurrent = 2;

        [Tooltip("Писать метрики оркестратора в лог при включённом GameLogFeature.Metrics в Game Log Settings.")]
        [SerializeField]
        private bool logAiOrchestrationMetrics;

        [Header("World Commands (Lua → MessagePipe → main thread)")]
        [Tooltip("Whitelist префабов, которые разрешено спавнить из Lua.")]
        [SerializeField]
        private CoreAiPrefabRegistryAsset worldPrefabRegistry;

        [Header("Network / AI authority")]
        [Tooltip("Где разрешён запуск LLM и оркестратора: все узлы, только хост или только чистые клиенты.")]
        [SerializeField]
        private AiNetworkExecutionPolicy aiNetworkExecutionPolicy = AiNetworkExecutionPolicy.AllPeers;

        [Tooltip("Опционально: компонент с ролью узла в сети (Netcode и т.д.). Пусто — одиночный хост.")]
        [SerializeField]
        private CoreAiNetworkPeerBehaviour networkPeerBehaviour;

        /// <summary>Регистрирует лог, промпты, LLM (маршрутизация + таймаут), оркестратор, Lua, память и entry points.</summary>
        protected override void Configure(IContainerBuilder builder)
        {
            if (gameLogSettings != null)
            {
                builder.RegisterInstance<IGameLogSettings>(gameLogSettings);
            }
            else
            {
                builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            }

            builder.RegisterAgentPrompts(agentPromptsManifest);
            builder.RegisterCore();

            if (worldPrefabRegistry != null)
            {
                builder.RegisterInstance(worldPrefabRegistry);
            }

            builder.Register<DefaultDataOverlayPayloadValidator>(Lifetime.Singleton).As<IDataOverlayPayloadValidator>();
            builder.Register<CoreAiVersioningLuaRuntimeBindings>(Lifetime.Singleton);
            builder.Register<CoreAiWorldLuaRuntimeBindings>(Lifetime.Singleton);
            builder.Register<AggregatingGameLuaRuntimeBindings>(Lifetime.Singleton).As<IGameLuaRuntimeBindings>();
            builder.Register<LoggingLuaExecutionObserver>(Lifetime.Singleton).As<ILuaExecutionObserver>();

            OpenAiHttpLlmSettings openAi = openAiHttpLlmSettings;
            LlmRoutingManifest routingManifest = llmRoutingManifest;
            float llmTimeout = llmRequestTimeoutSeconds;
            builder.Register(c =>
            {
                LlmClientRegistry reg = new(c.Resolve<IGameLogger>());
                reg.SetLegacyFallback(ResolveLlmClient(openAi, c.Resolve<IGameLogger>(), c.Resolve<IAgentMemoryStore>()));
                reg.ApplyManifest(routingManifest);
                return reg;
            }, Lifetime.Singleton).As<ILlmClientRegistry>().As<ILlmRoutingController>();
            builder.Register<ILlmClient>(c =>
                new LoggingLlmClientDecorator(
                    new RoutingLlmClient(c.Resolve<ILlmClientRegistry>()),
                    c.Resolve<IGameLogger>(),
                    llmTimeout), Lifetime.Singleton);

            builder.RegisterInstance(new AiOrchestrationQueueOptions
            {
                MaxConcurrent = aiOrchestrationMaxConcurrent < 1 ? 1 : aiOrchestrationMaxConcurrent
            });
            if (logAiOrchestrationMetrics)
            {
                builder.Register<IAiOrchestrationMetrics>(c =>
                        new LoggingAiOrchestrationMetrics(c.Resolve<IGameLogger>(), c.Resolve<IGameLogSettings>()),
                    Lifetime.Singleton);
            }
            else
            {
                builder.Register<IAiOrchestrationMetrics, NullAiOrchestrationMetrics>(Lifetime.Singleton);
            }

            if (networkPeerBehaviour != null)
            {
                builder.RegisterInstance<IAiNetworkPeer>(networkPeerBehaviour);
            }
            else
            {
                builder.Register<DefaultSoloNetworkPeer>(Lifetime.Singleton).As<IAiNetworkPeer>();
            }

            builder.Register<IAuthorityHost>(c =>
                    new NetworkedAuthorityHost(c.Resolve<IAiNetworkPeer>(), aiNetworkExecutionPolicy),
                Lifetime.Singleton);

            builder.RegisterCorePortable();
            // Runtime override: версии Lua Programmer на диск (оригинал / история / сброс).
            builder.Register(c => new FileLuaScriptVersionStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<ILuaScriptVersionStore>();
            builder.Register(c => new FileDataOverlayVersionStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<IDataOverlayVersionStore>();
            // Runtime override: сохраняем память на диск (по умолчанию включена только для Creator).
            builder.Register<FileAgentMemoryStore>(Lifetime.Singleton).As<IAgentMemoryStore>();
            builder.Register<CoreAiWorldCommandExecutor>(Lifetime.Singleton).As<ICoreAiWorldCommandExecutor>();

            // Game Config: Unity SO-based config store (override NullGameConfigStore from CorePortable)
            builder.Register(c => new UnityGameConfigStore(c.Resolve<IGameLogger>()), Lifetime.Singleton).As<IGameConfigStore>();

            builder.RegisterEntryPoint<AiGameCommandRouter>();
            builder.RegisterEntryPoint<CoreAIGameEntryPoint>();
        }

        /// <summary>Порядок выбора дублируется в Play Mode (сборка CoreAI.PlayModeTests): см. PlayModeProductionLikeLlmFactory.</summary>
        private static ILlmClient ResolveLlmClient(OpenAiHttpLlmSettings openAi, IGameLogger logger, IAgentMemoryStore memoryStore)
        {
            if (openAi != null && openAi.UseOpenAiCompatibleHttp)
            {
                return new OpenAiChatLlmClient(openAi);
            }
#if COREAI_NO_LLM
            return new StubLlmClient();
#else
            LLMAgent agent = FindFirstObjectByType<LLMAgent>();
            if (agent == null)
            {
                return new StubLlmClient();
            }

            // Если LLMUnity в сцене оставили без модели (GGUF путь пуст), она пишет ошибку и не поднимся.
            // Тогда безопаснее использовать stub и не пытаться дергать LLMUnity.
            LLM llm = agent.GetComponent<LLM>();
            if (llm != null)
            {
                LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llm, logger);
            }

            if (llm != null && string.IsNullOrWhiteSpace(llm.model))
            {
                return new StubLlmClient();
            }

            return new MeaiLlmUnityClient(agent, logger, memoryStore);
#endif
        }
    }
}