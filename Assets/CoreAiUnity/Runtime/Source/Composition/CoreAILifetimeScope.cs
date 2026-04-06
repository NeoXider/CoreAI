using CoreAI;
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
            "Единые настройки CoreAI: API-ключ, URL, модель, LLMUnity/HTTP переключение и др. Если null — ищется в Resources/CoreAISettings.")]
        [SerializeField]
        private CoreAISettingsAsset coreAiSettings;

        [Tooltip(
            "Если null — логируются все фичи (DefaultGameLogSettings). Иначе — фильтр по флагам и минимальному уровню.")]
        [SerializeField]
        private GameLogSettingsAsset gameLogSettings;

        [Tooltip("Опционально: переопределения и кастомные агенты (системный/user шаблоны из TextAsset).")]
        [SerializeField]
        private AgentPromptsManifest agentPromptsManifest;

        [Tooltip(
            "LEGACY: Если Use OpenAi Compatible Http включён — ILlmClient ходит в chat/completions. Лучше использовать CoreAI Settings.")]
        [SerializeField]
        private OpenAiHttpLlmSettings openAiHttpLlmSettings;

        [Tooltip(
            "Опционально: маршрутизация ILlmClient по роли (Enable Role Routing). Иначе — только legacy Open Ai + LLMUnity.")]
        [SerializeField]
        private LlmRoutingManifest llmRoutingManifest;

        [Tooltip("LEGACY: Автоотмена одного вызова ILlmClient.CompleteAsync, если модель «зависла». 0 — без ограничения. Лучше настраивать в CoreAI Settings.")]
        [SerializeField]
        private float llmRequestTimeoutSeconds = 15f;

        [Tooltip("LEGACY: Максимум параллельных задач. Лучше настраивать в CoreAI Settings.")]
        [SerializeField]
        [Min(1)]
        private int aiOrchestrationMaxConcurrent = 2;

        [Tooltip("LEGACY: Писать метрики оркестратора в лог. Лучше настраивать в CoreAI Settings.")]
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

        /// <summary>
        /// Получить настройки CoreAI. Приоритет: field → Resources → new instance.
        /// </summary>
        public CoreAISettingsAsset Settings
        {
            get
            {
                if (coreAiSettings != null) return coreAiSettings;
                coreAiSettings = CoreAISettingsAsset.Instance;
                return coreAiSettings;
            }
        }

        /// <summary>Регистрирует лог, промпты, LLM (маршрутизация + таймаут), оркестратор, Lua, память и entry points.</summary>
        protected override void Configure(IContainerBuilder builder)
        {
            // Инициализируем синглтон настроек
            CoreAISettingsAsset settings = Settings;
            if (settings != null)
            {
                CoreAISettingsAsset.SetInstance(settings);
                builder.RegisterInstance(settings);
                
                // Синхронизируем статические CoreAISettings с asset
                CoreAI.CoreAISettings.MaxLuaRepairGenerations = settings.MaxLuaRepairGenerations;
                CoreAI.CoreAISettings.MaxToolCallRetries = settings.MaxToolCallRetries;
                CoreAI.CoreAISettings.EnableMeaiDebugLogging = settings.EnableMeaiDebugLogging;
                CoreAI.CoreAISettings.ContextWindowTokens = settings.ContextWindowTokens;
                if (settings.RequestTimeoutSeconds > 0)
                {
                    CoreAI.CoreAISettings.LlmRequestTimeoutSeconds = settings.RequestTimeoutSeconds;
                }
            }

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

            // LLM Client регистрация с поддержкой CoreAISettingsAsset
            LlmRoutingManifest routingManifest = llmRoutingManifest;
            float llmTimeout = settings != null ? settings.LlmRequestTimeoutSeconds : llmRequestTimeoutSeconds;
            
            builder.Register(c =>
            {
                LlmClientRegistry reg = new(c.Resolve<IGameLogger>());
                reg.SetLegacyFallback(
                    ResolveLlmClient(settings, openAiHttpLlmSettings, c.Resolve<IGameLogger>(), c.Resolve<IAgentMemoryStore>()));
                reg.ApplyManifest(routingManifest);
                return reg;
            }, Lifetime.Singleton).As<ILlmClientRegistry>().As<ILlmRoutingController>();
            
            builder.Register<ILlmClient>(c =>
                new LoggingLlmClientDecorator(
                    new RoutingLlmClient(c.Resolve<ILlmClientRegistry>()),
                    c.Resolve<IGameLogger>(),
                    llmTimeout), Lifetime.Singleton);

            // Orchestrator настройки
            int maxConcurrent = settings != null ? settings.MaxConcurrentOrchestrations : aiOrchestrationMaxConcurrent;
            builder.RegisterInstance(new AiOrchestrationQueueOptions
            {
                MaxConcurrent = maxConcurrent < 1 ? 1 : maxConcurrent
            });
            
            bool logMetrics = settings != null ? settings.LogOrchestrationMetrics : logAiOrchestrationMetrics;
            if (logMetrics)
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
            builder.Register(c => new UnityGameConfigStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<IGameConfigStore>();

            builder.RegisterEntryPoint<AiGameCommandRouter>();
            builder.RegisterEntryPoint<CoreAIGameEntryPoint>();
        }

        /// <summary>
        /// Порядок выбора: CoreAISettingsAsset (Auto/LlmUnity/OpenAiHttp/NoLlm) → legacy OpenAiHttpLlmSettings → LLMUnity → Stub.
        /// Дублируется в Play Mode (сборка CoreAI.PlayModeTests): см. PlayModeProductionLikeLlmFactory.
        /// </summary>
        private static ILlmClient ResolveLlmClient(
            CoreAISettingsAsset settings,
            OpenAiHttpLlmSettings legacyOpenAi,
            IGameLogger logger,
            IAgentMemoryStore memoryStore)
        {
            // Приоритет 1: CoreAISettingsAsset
            if (settings != null)
            {
                switch (settings.BackendType)
                {
                    case LlmBackendType.OpenAiHttp:
                        return new OpenAiChatLlmClient(settings);
                    case LlmBackendType.Offline:
                        return new OfflineLlmClient(settings);
                    case LlmBackendType.Auto:
                        // Auto: пробуем LLMUnity, fallback на Stub
                        return TryResolveAutoClient(settings, legacyOpenAi, logger, memoryStore);
                    case LlmBackendType.LlmUnity:
                        return ResolveLlmUnityClient(settings, logger, memoryStore);
                }
            }

            // Приоритет 2: Legacy OpenAiHttpLlmSettings
            if (legacyOpenAi != null && legacyOpenAi.UseOpenAiCompatibleHttp)
            {
                return new OpenAiChatLlmClient(legacyOpenAi);
            }

            // Приоритет 3: LLMUnity или Stub
#if COREAI_NO_LLM
            return new StubLlmClient();
#else
            return ResolveLlmUnityClient(settings, logger, memoryStore);
#endif
        }

        /// <summary>
        /// Auto режим: приоритет из CoreAISettingsAsset.AutoPriority.
        /// </summary>
        private static ILlmClient TryResolveAutoClient(
            CoreAISettingsAsset settings,
            OpenAiHttpLlmSettings legacyOpenAi,
            IGameLogger logger,
            IAgentMemoryStore memoryStore)
        {
#if COREAI_NO_LLM
            return new StubLlmClient();
#else
            bool httpFirst = settings != null && settings.AutoPriority == LlmAutoPriority.HttpFirst;

            if (httpFirst)
            {
                // HTTP API → LLMUnity → Offline
                var httpClient = TryResolveHttpApiClient(settings);
                if (httpClient != null)
                {
                    return httpClient;
                }

                var llmUnityClient = TryResolveLlmUnityClient(settings, logger, memoryStore);
                if (llmUnityClient != null)
                {
                    return llmUnityClient;
                }

                return new OfflineLlmClient(settings);
            }
            else
            {
                // LLMUnity → HTTP API → Offline (по умолчанию)
                var llmUnityClient = TryResolveLlmUnityClient(settings, logger, memoryStore);
                if (llmUnityClient != null)
                {
                    return llmUnityClient;
                }

                var httpClient2 = TryResolveHttpApiClient(settings);
                if (httpClient2 != null)
                {
                    return httpClient2;
                }

                return new OfflineLlmClient(settings);
            }
#endif
        }

        /// <summary>
        /// Попытка создать HTTP API клиент. Возвращает null если не удалось.
        /// </summary>
        private static ILlmClient TryResolveHttpApiClient(CoreAISettingsAsset settings)
        {
            if (settings != null && settings.UseHttpApi && !string.IsNullOrEmpty(settings.ApiBaseUrl) && !string.IsNullOrEmpty(settings.ModelName))
            {
                return new OpenAiChatLlmClient(settings);
            }
            return null;
        }

        /// <summary>
        /// Попытка создать LLMUnity клиент. Возвращает null если не удалось.
        /// </summary>
        private static ILlmClient TryResolveLlmUnityClient(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore)
        {
            LLMAgent agent = null;

            // Если в настройках указано имя агента — ищем по имени
            if (settings != null && !string.IsNullOrWhiteSpace(settings.LlmUnityAgentName))
            {
                GameObject go = GameObject.Find(settings.LlmUnityAgentName);
                if (go != null)
                {
                    agent = go.GetComponent<LLMAgent>();
                }
            }

            // Fallback: ищем первый LLMAgent на сцене
            if (agent == null)
            {
                agent = FindFirstObjectByType<LLMAgent>();
            }

            if (agent == null)
            {
                return null;
            }

            LLM llm = agent.GetComponent<LLM>();
            if (llm != null && settings != null && settings.LlmUnityDontDestroyOnLoad)
            {
                llm.dontDestroyOnLoad = true;
            }

            // Автоназначение модели из LLMUnity Model Manager
            if (llm != null)
            {
                LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llm, logger);
            }

            // Если модель не назначена — не можем использовать LLMUnity
            if (llm != null && string.IsNullOrWhiteSpace(llm.model))
            {
                return null;
            }

            return new MeaiLlmUnityClient(agent, logger, memoryStore);
        }

        /// <summary>
        /// Создать LLMUnity клиент. Fallback на Stub если не удалось.
        /// </summary>
        private static ILlmClient ResolveLlmUnityClient(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            IAgentMemoryStore memoryStore)
        {
            var client = TryResolveLlmUnityClient(settings, logger, memoryStore);
            return client ?? new StubLlmClient();
        }
    }
}