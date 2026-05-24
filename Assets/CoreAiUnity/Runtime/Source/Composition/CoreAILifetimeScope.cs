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
using CoreAI.Infrastructure;
using System.IO;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Unity lifetime scope that wires CoreAI runtime services and scene assets.
    /// </summary>
    public sealed class CoreAILifetimeScope : LifetimeScope
    {
        [Tooltip("Shared CoreAI settings asset. If null, Resources/CoreAISettings is used.")]
        [SerializeField]
        private CoreAISettingsAsset coreAiSettings;

        [Tooltip("Optional log settings asset. If null, DefaultGameLogSettings is used.")]
        [SerializeField]
        private GameLogSettingsAsset gameLogSettings;

        [Tooltip("Optional prompt overrides and custom agents loaded from TextAssets.")]
        [SerializeField]
        private AgentPromptsManifest agentPromptsManifest;

        [Tooltip("Optional ILlmClient routing by role. When null, legacy routing is used.")]
        [SerializeField]
        private LlmRoutingManifest llmRoutingManifest;

        [Header("World Commands (Lua -> MessagePipe -> main thread)")]
        [Tooltip("Prefab whitelist that Lua world commands are allowed to spawn.")]
        [SerializeField]
        private CoreAiPrefabRegistryAsset worldPrefabRegistry;

        [Header("Network / AI authority")]
        [Tooltip("Controls where LLM and orchestration execution is allowed.")]
        [SerializeField]
        private AiNetworkExecutionPolicy aiNetworkExecutionPolicy = AiNetworkExecutionPolicy.AllPeers;

        [Tooltip("Optional network peer role provider, for example Netcode. Empty means a standalone host.")]
        [SerializeField]
        private CoreAiNetworkPeerBehaviour networkPeerBehaviour;

        /// <summary>
        ///
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

        /// <summary>Registers CoreAI services into the VContainer lifetime scope.</summary>
        protected override void Configure(IContainerBuilder builder)
        {
            // No-op guard before a conditional operation.
            CoreAISettingsAsset settings = Settings;
            if (settings != null)
            {
                CoreAISettingsAsset.SetInstance(settings);
                // No-op guard before a conditional operation.
                // No-op guard before a conditional operation.
                builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);

                // No-op guard before a conditional operation.
                CoreAISettings.Instance = settings;
            }

            // Skip processing when the checked condition is already satisfied.
            if (gameLogSettings != null)
            {
                builder.RegisterInstance<IGameLogSettings>(gameLogSettings);
            }
            else
            {
                builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            }

            // No-op guard before a conditional operation.
            builder.RegisterAgentPrompts(agentPromptsManifest);
            builder.RegisterCore();

            // No-op guard before a conditional operation.
            builder.RegisterWorldCommands(worldPrefabRegistry);

            // No-op guard before a conditional operation.
            builder.RegisterLlmPipeline(settings, llmRoutingManifest);

            // Skip processing when the checked condition is already satisfied.
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

            RegisterConversationSummaryForCoreAiLifetimeScope(builder);

            // No-op guard before a conditional operation.
            builder.Register(c => new FileLuaScriptVersionStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<ILuaScriptVersionStore>();
            builder.Register(c => new FileDataOverlayVersionStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<IDataOverlayVersionStore>();
            // File-backed agent memory on all players. WebGL: IDBFS + CoreAi_PersistFsSync (jslib) after writes
            // so chat/memory JSON survives reload when Application.Quit does not run (tab close).
            RegisterAgentMemoryStore(builder);

            // No-op guard before a conditional operation.
            builder.RegisterEntryPoint<AiGameCommandRouter>();
            builder.RegisterEntryPoint<CoreAIGameEntryPoint>();
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            if (settings != null)
            {
                builder.RegisterEntryPoint<LlmUnityAutostartEntryPoint>();
            }
#endif
        }

#if UNITY_WEBGL
        internal const bool UsesPersistentFileConversationSummaryStore = false;
#else
        internal const bool UsesPersistentFileConversationSummaryStore = true;
#endif

        /// <summary>
        /// Registers <see cref="IConversationSummaryStore"/> for this lifetime scope.
        /// Non-WebGL builds persist summaries under <c>Application.persistentDataPath/CoreAI/ConversationSummaries</c>.
        /// WebGL uses <see cref="InMemoryConversationSummaryStore"/> because synchronous <see cref="File"/> IO maps to IndexedDB and stalls the main loop each turn.
        /// </summary>
        internal static void RegisterConversationSummaryForCoreAiLifetimeScope(IContainerBuilder builder)
        {
#if !UNITY_WEBGL
            builder.Register<IConversationSummaryStore>(_ =>
                    new FileConversationSummaryStore(
                        Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                            CoreAiPersistentPaths.ConversationSummaries),
                        null),
                Lifetime.Singleton);

            builder.RegisterCorePortable(
                true,
                true);
#else
            builder.RegisterCorePortable(
                suppressDefaultConversationSummaryStore: false,
                suppressDefaultAgentMemoryStore: true);
#endif
        }

        /// <summary>
        /// Registers <see cref="FileAgentMemoryStore"/> as <see cref="IAgentMemoryStore"/> and
        /// <see cref="IConversationTranscriptStore"/>. Called from <see cref="Configure"/>; internal for EditMode DI tests.
        /// </summary>
        internal static void RegisterAgentMemoryStore(IContainerBuilder builder)
        {
            builder.Register<FileAgentMemoryStore>(Lifetime.Singleton)
                .As<IAgentMemoryStore>()
                .As<IConversationTranscriptStore>();
        }
    }
}
