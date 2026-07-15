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
using CoreAI.Unity;
using System.IO;
using UnityEngine;
using UnityEngine.Serialization;
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

        [Header("Optional Modules")]
        [Tooltip("Optional child module that owns Lua and world-command configuration. A child component is auto-discovered when this reference is empty.")]
        [SerializeField]
        private CoreAiLuaWorldModule luaWorldModule;

        [HideInInspector]
        [FormerlySerializedAs("legacyWorldPrefabRegistry")]
        [SerializeField]
        private CoreAiPrefabRegistryAsset worldPrefabRegistry;

        [HideInInspector]
        [FormerlySerializedAs("luaAllowedScenes")]
        [SerializeField]
        private string[] legacyLuaAllowedScenes = System.Array.Empty<string>();

        [HideInInspector]
        [FormerlySerializedAs("enableFullLuaAccess")]
        [SerializeField]
        private bool legacyEnableFullLuaAccess;

        /// <summary>
        /// Whether this composition grants the Full Lua tier (unity_* reflection). Exposed so
        /// scene helpers that autoload persisted mods (e.g. the mods-chat persistence demo)
        /// can grant the SAME tier the host does instead of hardcoding a lower one.
        /// </summary>
        public bool FullLuaAccessEnabled => ResolveLuaWorldModule() != null
            ? ResolveLuaWorldModule().FullAccessEnabled
            : legacyEnableFullLuaAccess;

        [HideInInspector]
        [FormerlySerializedAs("enableFullLuaPrivateAccess")]
        [SerializeField]
        private bool legacyEnableFullLuaPrivateAccess;

        /// <summary>The explicit or auto-discovered Lua/world-command child module.</summary>
        public CoreAiLuaWorldModule LuaWorldModule => ResolveLuaWorldModule();

        [Header("Skills")]
        [Tooltip("On-demand skills per agent role: the role gets a read_skill catalog with these " +
                 "SkillSetAssets (instructions from a TextAsset or inline text). No code needed.")]
        [SerializeField]
        private RoleSkillsBinding[] roleSkills = System.Array.Empty<RoleSkillsBinding>();

        /// <summary>Inspector row binding one agent role to its on-demand skill assets.</summary>
        [System.Serializable]
        public sealed class RoleSkillsBinding
        {
            [Tooltip("Agent role id, e.g. Programmer, SmartChat, or a custom role.")]
            public string roleId = BuiltInAgentRoleIds.Programmer;

            [Tooltip("Skills this role can load on demand via read_skill.")]
            public SkillSetAsset[] skills = System.Array.Empty<SkillSetAsset>();
        }

        [Header("Network / AI authority")]
        [Tooltip("Controls where LLM and orchestration execution is allowed.")]
        [SerializeField]
        private AiNetworkExecutionPolicy aiNetworkExecutionPolicy = AiNetworkExecutionPolicy.AllPeers;

        [Tooltip("Optional network peer role provider, for example Netcode. Empty means a standalone host.")]
        [SerializeField]
        private CoreAiNetworkPeerBehaviour networkPeerBehaviour;

        /// <summary>
        /// Effective settings asset for this scope, falling back to the Resources singleton when
        /// no scene-specific asset is assigned.
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

        /// <summary>
        /// Fails fast when no <see cref="CoreAISettingsAsset"/> resolves. The LLM DI graph
        /// ctor-injects the concrete <see cref="CoreAISettingsAsset"/> (ConfigurableLlmAgentProvider),
        /// so a missing asset must surface here with an actionable message instead of a generic
        /// VContainer resolve error later. A synthesized default is deliberately avoided: it would
        /// only mask the misconfiguration behind blank endpoints/keys. Internal for EditMode tests.
        /// </summary>
        internal static void EnsureSettingsPresent(CoreAISettingsAsset settings)
        {
            if (settings == null)
            {
                throw new System.InvalidOperationException(
                    "CoreAISettings asset missing — add Resources/CoreAISettings or assign one on the CoreAILifetimeScope.");
            }
        }

        /// <summary>Registers CoreAI services into the VContainer lifetime scope.</summary>
        protected override void Configure(IContainerBuilder builder)
        {
            CoreAISettingsAsset settings = Settings;
            EnsureSettingsPresent(settings);

            if (settings != null)
            {
                CoreAISettingsAsset.SetInstance(settings);
                builder.RegisterInstance<ICoreAISettings, CoreAISettingsAsset>(settings);

                CoreAISettings.Instance = settings;
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

            CoreAiLuaWorldModule module = ResolveLuaWorldModule();
            if (module != null)
            {
                module.Register(builder);
            }
            else
            {
                // WHY: Existing scenes keep their behavior until migrated to the child module.
                // WHY: New scopes without the optional module retain the safe legacy defaults.
                builder.RegisterWorldCommands(
                    worldPrefabRegistry,
                    legacyLuaAllowedScenes,
                    legacyEnableFullLuaAccess,
                    legacyEnableFullLuaPrivateAccess);
            }

            builder.RegisterLlmPipeline(settings, llmRoutingManifest);

            if (roleSkills is { Length: > 0 })
            {
                RoleSkillsBinding[] bindings = roleSkills;
                builder.RegisterBuildCallback(container =>
                {
                    AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();
                    foreach (RoleSkillsBinding binding in bindings)
                    {
                        if (binding?.skills == null || string.IsNullOrWhiteSpace(binding.roleId))
                        {
                            continue;
                        }

                        foreach (SkillSetAsset asset in binding.skills)
                        {
                            if (asset != null)
                            {
                                policy.AddSkillForRole(binding.roleId, asset.BuildSkillSet());
                            }
                        }
                    }
                });
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

            RegisterConversationSummaryForCoreAiLifetimeScope(builder);

            builder.Register(c => new FileLuaScriptVersionStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<ILuaScriptVersionStore>();
            builder.Register(c => new FileDataOverlayVersionStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<IDataOverlayVersionStore>();
            // WHY: File-backed agent memory on all players. WebGL: IDBFS + CoreAi_PersistFsSync (jslib) after writes
            // so chat/memory JSON survives reload when Application.Quit does not run (tab close).
            RegisterAgentMemoryStore(builder);

            builder.RegisterEntryPoint<AiGameCommandRouter>();
            builder.RegisterEntryPoint<CoreAIGameEntryPoint>();
            builder.RegisterEntryPoint<WorldStateEntryPoint>();
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            if (settings != null)
            {
                builder.RegisterEntryPoint<LlmUnityAutostartEntryPoint>();
            }
#endif
        }

        private CoreAiLuaWorldModule ResolveLuaWorldModule()
        {
            if (luaWorldModule == null)
            {
                luaWorldModule = GetComponentInChildren<CoreAiLuaWorldModule>(true);
            }

            return luaWorldModule;
        }

        internal void SetLuaWorldModuleForMigration(CoreAiLuaWorldModule module)
        {
            luaWorldModule = module;
            if (module != null)
            {
                worldPrefabRegistry = null;
                legacyLuaAllowedScenes = System.Array.Empty<string>();
                legacyEnableFullLuaAccess = false;
                legacyEnableFullLuaPrivateAccess = false;
            }
        }

        internal void CopyLegacyLuaWorldConfigurationTo(CoreAiLuaWorldModule module)
        {
            if (module == null)
            {
                throw new System.ArgumentNullException(nameof(module));
            }

            module.ConfigureForMigration(
                worldPrefabRegistry,
                legacyLuaAllowedScenes,
                legacyEnableFullLuaAccess,
                legacyEnableFullLuaPrivateAccess);
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
            builder.Register<ITokenCalibrationStore>(_ =>
                    new FileTokenCalibrationStore(
                        Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                            "TokenCalibration", "scales.json"),
                        null),
                Lifetime.Singleton);
            builder.Register<IConversationSummaryStore>(_ =>
                    new FileConversationSummaryStore(
                        Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                            CoreAiPersistentPaths.ConversationSummaries),
                        null),
                Lifetime.Singleton);

            builder.RegisterCorePortable(
                true,
                true,
                true);
#else
            builder.RegisterCorePortable(
                suppressDefaultConversationSummaryStore: false,
                suppressDefaultAgentMemoryStore: true,
                suppressDefaultTokenCalibrationStore: false);
#endif
        }

        /// <summary>
        /// Registers <see cref="FileAgentMemoryStore"/> as <see cref="IAgentMemoryStore"/> and
        /// <see cref="IConversationTranscriptStore"/>. Called from <see cref="Configure"/>; internal for EditMode DI tests.
        /// </summary>
        internal static void RegisterAgentMemoryStore(IContainerBuilder builder)
        {
            // WHY: Lambda registration: the ctor's optional string rootDirectory must not be constructor-injected.
            builder.Register(_ => new FileAgentMemoryStore(maxChatHistoryMessages: 500,
                    maxTranscriptEntries: 2000), Lifetime.Singleton)
                .As<IAgentMemoryStore>()
                .As<IConversationTranscriptStore>();
        }
    }
}
