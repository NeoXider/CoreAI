using CoreAI;
using CoreAI.Ai;
using CoreAI.Infrastructure.Ai;
using CoreAI.Infrastructure.AiMemory;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Messaging;
using CoreAI.Infrastructure.Prompts;
using CoreAI.Infrastructure.World;
using CoreAI.Infrastructure.Lua;
using CoreAI.Authority;
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
            "Опционально: маршрутизация ILlmClient по роли (Enable Role Routing). Иначе — только legacy Open Ai + LLMUnity.")]
        [SerializeField]
        private LlmRoutingManifest llmRoutingManifest;

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
                if (coreAiSettings != null)
                {
                    return coreAiSettings;
                }

                coreAiSettings = CoreAISettingsAsset.Instance;
                return coreAiSettings;
            }
        }

        /// <summary>Регистрирует лог, промпты, LLM (маршрутизация + таймаут), оркестратор, Lua, память и entry points.</summary>
        protected override void Configure(IContainerBuilder builder)
        {
            // ── 1. Settings ────────────────────────────────────────────────
            CoreAISettingsAsset settings = Settings;
            if (settings != null)
            {
                CoreAISettingsAsset.SetInstance(settings);
                builder.RegisterInstance<ICoreAISettings>(settings);

                // Статический прокси делегирует в DI-экземпляр автоматически
                CoreAISettings.Instance = settings;
            }

            // ── 2. Logging ─────────────────────────────────────────────────
            if (gameLogSettings != null)
            {
                builder.RegisterInstance<IGameLogSettings>(gameLogSettings);
            }
            else
            {
                builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            }

            // ── 3. Agent Prompts ───────────────────────────────────────────
            builder.RegisterAgentPrompts(agentPromptsManifest);
            builder.RegisterCore();

            // ── 4. World Commands (Lua, prefabs, config) ───────────────────
            builder.RegisterWorldCommands(worldPrefabRegistry);

            // ── 5. LLM Pipeline (clients, routing, orchestration) ──────────
            builder.RegisterLlmPipeline(settings, llmRoutingManifest);

            // ── 6. Network Authority ───────────────────────────────────────
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

            // ── 7. Core Portable (orchestrator, agent memory, Lua sandbox) ─
            builder.RegisterCorePortable();

            // Runtime overrides: файловые версии скриптов и агентной памяти
            builder.Register(c => new FileLuaScriptVersionStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<ILuaScriptVersionStore>();
            builder.Register(c => new FileDataOverlayVersionStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<IDataOverlayVersionStore>();
            builder.Register<FileAgentMemoryStore>(Lifetime.Singleton).As<IAgentMemoryStore>();

            // ── 8. Entry Points ────────────────────────────────────────────
            builder.RegisterEntryPoint<AiGameCommandRouter>();
            builder.RegisterEntryPoint<CoreAIGameEntryPoint>();
        }
    }
}
