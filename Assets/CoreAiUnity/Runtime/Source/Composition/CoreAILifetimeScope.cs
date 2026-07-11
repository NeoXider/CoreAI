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

        [Tooltip("Scene names Lua may load via coreai_world_load_scene. Empty = any Build Settings scene (legacy).")]
        [SerializeField]
        private string[] luaAllowedScenes = System.Array.Empty<string>();

        [Tooltip("When enabled, Lua with Full capability can access arbitrary GameObjects/components via reflection.")]
        [SerializeField]
        private bool enableFullLuaAccess;

        /// <summary>
        /// Whether this composition grants the Full Lua tier (unity_* reflection). Exposed so
        /// scene helpers that autoload persisted mods (e.g. the mods-chat persistence demo)
        /// can grant the SAME tier the host does instead of hardcoding a lower one.
        /// </summary>
        public bool FullLuaAccessEnabled => enableFullLuaAccess;

        [Tooltip("When enabled, Full-tier Lua reflection can access non-public members.")]
        [SerializeField]
        private bool enableFullLuaPrivateAccess;

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

        /// <summary>Registers CoreAI services into the VContainer lifetime scope.</summary>
        protected override void Configure(IContainerBuilder builder)
        {
            CoreAISettingsAsset settings = Settings;
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

            // WHY: Full tier works on WebGL/IL2CPP too: the Lua-CSharp runtime is a managed, AOT-safe VM, so
            // there is no wasm "null function" trap to gate around. link.xml preserves the Lua assemblies
            // plus the Resources/TextAsset members CoreAI loads prompts/skills/bundled mods through.
            builder.RegisterWorldCommands(
                worldPrefabRegistry,
                luaAllowedScenes,
                enableFullLuaAccess,
                enableFullLuaPrivateAccess);

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
            builder.Register(_ => new FileAgentMemoryStore(), Lifetime.Singleton)
                .As<IAgentMemoryStore>()
                .As<IConversationTranscriptStore>();
        }
    }
}
