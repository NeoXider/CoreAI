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
using CoreAI.Logging;
using CoreAI.Unity;
using System.IO;
using UnityEngine;
using UnityEngine.Serialization;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>Backing-store lifetime for agent memory and conversation state owned by a Unity host.</summary>
    public enum AgentMemoryPersistenceMode
    {
        /// <summary>Persist memory/chat/transcripts and, outside WebGL, summaries across launches.</summary>
        Persistent = 0,

        /// <summary>Keep memory, flat chat, structured transcripts, and summaries in process memory only.</summary>
        SessionOnly = 1
    }

    /// <summary>
    /// Inspector-assignable bridge for a host-owned <see cref="IAgentMemoryScopeProvider"/>.
    /// Derive a project component from this type and return the current tenant/user/session/topic scope.
    /// </summary>
    public abstract class AgentMemoryScopeProviderBehaviour : MonoBehaviour, IAgentMemoryScopeProvider
    {
        /// <inheritdoc />
        public abstract AgentMemoryScope GetScope(string roleId);
    }

    /// <summary>
    /// Unity lifetime scope that wires CoreAI runtime services and scene assets.
    /// </summary>
    public sealed class CoreAILifetimeScope : LifetimeScope
    {
        [Tooltip("Shared CoreAI settings asset. If null, Resources/CoreAISettings is used.")]
        [SerializeField]
        private CoreAISettingsAsset coreAiSettings;

        [Tooltip("Optional log settings asset; authoring source for the runtime filter. " +
                 "If null, every category is logged from level Info and a warning is issued.")]
        [SerializeField]
        private GameLogSettingsAsset gameLogSettings;

        [Tooltip("Optional prompt overrides and custom agents loaded from TextAssets.")]
        [SerializeField]
        private AgentPromptsManifest agentPromptsManifest;

        [Tooltip("Optional ILlmClient routing by role. When null, legacy routing is used.")]
        [SerializeField]
        private LlmRoutingManifest llmRoutingManifest;

        [Header("Optional Modules")]
        [Tooltip(
            "Optional child module that owns Lua and world-command configuration. A child component is auto-discovered when this reference is empty.")]
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

        [Header("Memory isolation")]
        [Tooltip("Persistent writes memory/conversation state across launches. SessionOnly keeps all student " +
                 "memory, chat, transcript, and summary state in process memory and creates no memory files.")]
        [SerializeField]
        private AgentMemoryPersistenceMode agentMemoryPersistenceMode = AgentMemoryPersistenceMode.Persistent;

        [Tooltip("Optional host component that returns the current tenant/user/session/topic memory scope. " +
                 "Leave empty only for a single-user process that intentionally keeps legacy role-only keys.")]
        [SerializeField]
        private AgentMemoryScopeProviderBehaviour agentMemoryScopeProvider;

        [Tooltip("How many chat messages one role keeps per memory key. Beyond it the OLDEST turns are dropped " +
                 "on every new message; the store logs the first drop and raises FileAgentMemoryStore.HistoryTrimmed.")]
        [Min(1)]
        [SerializeField]
        private int chatHistoryMessageCap = FileAgentMemoryStore.DefaultMaxChatHistoryMessages;

        [Tooltip("How many structured transcript rows one role keeps per memory key (chat lines plus tool rows).")]
        [Min(1)]
        [SerializeField]
        private int transcriptEntryCap = FileAgentMemoryStore.DefaultMaxTranscriptEntries;

        [System.NonSerialized]
        private IAgentMemoryScopeProvider runtimeAgentMemoryScopeProvider;

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
        /// The code-supplied provider, then the inspector component, or <c>null</c> when the portable
        /// <see cref="DefaultAgentMemoryScopeProvider"/> should preserve legacy role-only keys.
        /// </summary>
        public IAgentMemoryScopeProvider ConfiguredAgentMemoryScopeProvider =>
            runtimeAgentMemoryScopeProvider ?? agentMemoryScopeProvider;

        /// <summary>The backing-store mode that will be applied when this scope builds.</summary>
        public AgentMemoryPersistenceMode ConfiguredAgentMemoryPersistenceMode => agentMemoryPersistenceMode;

        /// <summary>
        /// Selects persistent or process-only agent/conversation storage before the container is built.
        /// Call this while the scope GameObject is inactive, then activate it.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">The container is already built.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">The enum value is invalid.</exception>
        public void SetAgentMemoryPersistenceMode(AgentMemoryPersistenceMode mode)
        {
            if (Container != null)
            {
                throw new System.InvalidOperationException(
                    "SetAgentMemoryPersistenceMode must be called before CoreAILifetimeScope builds its container. " +
                    "Configure it on an inactive GameObject, then activate the scope.");
            }

            if (!System.Enum.IsDefined(typeof(AgentMemoryPersistenceMode), mode))
            {
                throw new System.ArgumentOutOfRangeException(nameof(mode), mode,
                    "Unknown agent memory persistence mode.");
            }

            agentMemoryPersistenceMode = mode;
        }

        /// <summary>Потолок сообщений чата на роль, который получит backing store при сборке.</summary>
        public int ConfiguredChatHistoryMessageCap => chatHistoryMessageCap;

        /// <summary>Потолок строк транскрипта на роль, который получит backing store при сборке.</summary>
        public int ConfiguredTranscriptEntryCap => transcriptEntryCap;

        /// <summary>
        /// Задаёт потолки переписки до сборки контейнера. Раньше 500/2000 были зашиты в регистрацию:
        /// курс ученика — сотни ходов, и после потолка начало переписки исчезало при каждом новом
        /// сообщении без возможности это поменять. Понижение потолков применяется к уже сохранённой
        /// истории при следующей загрузке роли.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">The container is already built.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">A cap is below one.</exception>
        public void SetConversationHistoryCaps(int maxChatHistoryMessages, int maxTranscriptEntries)
        {
            if (Container != null)
            {
                throw new System.InvalidOperationException(
                    "SetConversationHistoryCaps must be called before CoreAILifetimeScope builds its container. " +
                    "Configure it on an inactive GameObject, then activate the scope.");
            }

            if (maxChatHistoryMessages < 1)
            {
                throw new System.ArgumentOutOfRangeException(nameof(maxChatHistoryMessages), maxChatHistoryMessages,
                    "At least one chat message must be kept.");
            }

            if (maxTranscriptEntries < 1)
            {
                throw new System.ArgumentOutOfRangeException(nameof(maxTranscriptEntries), maxTranscriptEntries,
                    "At least one transcript row must be kept.");
            }

            chatHistoryMessageCap = maxChatHistoryMessages;
            transcriptEntryCap = maxTranscriptEntries;
        }

        /// <summary>
        /// Supplies a host-owned memory scope provider before this lifetime scope builds its container.
        /// Call this while the scope GameObject is inactive, then activate it. Passing <c>null</c> clears the
        /// code override and falls back to the inspector component or the legacy empty scope.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">
        /// Thrown when the container is already built; changing a provider afterwards would split one process
        /// across two incompatible key spaces.
        /// </exception>
        public void SetAgentMemoryScopeProvider(IAgentMemoryScopeProvider provider)
        {
            if (Container != null)
            {
                throw new System.InvalidOperationException(
                    "SetAgentMemoryScopeProvider must be called before CoreAILifetimeScope builds its container. " +
                    "Configure it on an inactive GameObject, then activate the scope.");
            }

            runtimeAgentMemoryScopeProvider = provider;
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
                GameLogFilter.UseAuthoredSettings(gameLogSettings.ToOptions());
            }
            else
            {
                GameLogFilter.UseAuthoredSettings(null);
                GameLoggerUnscopedFallback.Instance.LogWarning(
                    GameLogFeature.Composition,
                    "No Game Log Settings asset is assigned on CoreAILifetimeScope — logging falls back to " +
                    "every category at minimum level Info. Assign an asset (CoreAI/Logging/Game Log Settings) " +
                    "or call GameLogFilter to change the filter at runtime.",
                    this);
            }

            // WHY: The container and the unscoped fallback logger must share one live settings instance,
            // otherwise runtime GameLogFilter changes would only reach half of the call sites. The asset
            // itself is never registered: it stays the authoring source and must not be mutated at runtime.
            builder.RegisterInstance<IGameLogSettings>(GameLogFilter.Settings);

            builder.RegisterAgentPrompts(agentPromptsManifest);
            builder.RegisterCore();

            CoreAiLuaWorldModule module = ResolveLuaWorldModule();
            if (module != null)
            {
                module.Register(builder);
            }
            else
            {
                // WHY: no optional Lua world module resolved — register the legacy world-command
                // defaults directly so existing and new scenes without the module keep working.
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

            RegisterAgentMemoryScopeProvider(builder, ConfiguredAgentMemoryScopeProvider);
            RegisterConversationSummaryForCoreAiLifetimeScope(builder, agentMemoryPersistenceMode);

            builder.Register(c => new FileLuaScriptVersionStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<ILuaScriptVersionStore>();
            builder.Register(c => new FileDataOverlayVersionStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<IDataOverlayVersionStore>();
            // WHY: Persistent uses the file-backed store on all players (WebGL flushes through IDBFS).
            // SessionOnly swaps the private backing to process memory while keeping the same scoped facades.
            RegisterAgentMemoryStore(builder, agentMemoryPersistenceMode, chatHistoryMessageCap, transcriptEntryCap);

            builder.RegisterEntryPoint<AiGameCommandRouter>();
            builder.RegisterEntryPoint<CoreAIGameEntryPoint>();
            builder.RegisterEntryPoint<WorldStateEntryPoint>();
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            if (settings != null && ShouldRegisterLlmUnityAutostart(Application.platform))
            {
                builder.RegisterEntryPoint<LlmUnityAutostartEntryPoint>();
            }
#endif
        }

        internal static bool ShouldRegisterLlmUnityAutostart(RuntimePlatform platform)
        {
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            return LocalModelPlatformSupport.IsSupported(platform);
#else
            return false;
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

        /// <summary>
        /// Registers <see cref="IConversationSummaryStore"/> for this lifetime scope.
        /// <see cref="AgentMemoryPersistenceMode.Persistent"/> uses
        /// <c>Application.persistentDataPath/CoreAI/ConversationSummaries</c> on EVERY player, WebGL included;
        /// <see cref="AgentMemoryPersistenceMode.SessionOnly"/> uses an in-memory backing.
        /// <para>
        /// WHY WebGL тоже файловый: сводка — это пересказ всего, что старше окна истории. Оперативное
        /// хранилище на WebGL означало, что после перезагрузки вкладки сводка пуста, начало урока исчезает,
        /// а следующая компакция суммирует тот же префикс заново — лишний оплаченный вызов LLM. Прежнее
        /// обоснование («синхронный File IO на WebGL идёт в IndexedDB и стопорит цикл») не соответствует
        /// устройству платформы: <c>persistentDataPath</c> там — MEMFS в памяти вкладки, в IndexedDB его
        /// доводит только асинхронный <c>FS.syncfs</c>, а <see cref="FileAgentMemoryStore"/> те же
        /// синхронные вызовы делает на WebGL каждый ход. Единственное, чего портативному стору не хватало, —
        /// постановка флаша в очередь после записи; она передаётся хуком <c>afterWrite</c>.
        /// </para>
        /// </summary>
        internal static void RegisterConversationSummaryForCoreAiLifetimeScope(
            IContainerBuilder builder,
            AgentMemoryPersistenceMode mode = AgentMemoryPersistenceMode.Persistent)
        {
#if !UNITY_WEBGL
            builder.Register<ITokenCalibrationStore>(_ =>
                    new FileTokenCalibrationStore(
                        Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                            "TokenCalibration", "scales.json"),
                        null),
                Lifetime.Singleton);
            const bool suppressDefaultTokenCalibrationStore = true;
#else
            const bool suppressDefaultTokenCalibrationStore = false;
#endif
            if (mode == AgentMemoryPersistenceMode.Persistent)
            {
                builder.Register(c =>
                        new FileConversationSummaryStore(
                            Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                                CoreAiPersistentPaths.ConversationSummaries),
                            ResolveLogOrNull(c),
                            CoreAiWebGlPersistence.Sync),
                        Lifetime.Singleton)
                    .AsSelf();
                builder.Register<IConversationSummaryStore>(c =>
                        new ScopedConversationSummaryStoreDecorator(
                            c.Resolve<FileConversationSummaryStore>(),
                            c.Resolve<IAgentMemoryScopeProvider>()),
                    Lifetime.Singleton);
            }
            else if (mode == AgentMemoryPersistenceMode.SessionOnly)
            {
                RegisterInMemoryConversationSummaryStore(builder);
            }
            else
            {
                throw new System.ArgumentOutOfRangeException(nameof(mode), mode,
                    "Unknown agent memory persistence mode.");
            }

            builder.RegisterCorePortable(
                true,
                true,
                suppressDefaultTokenCalibrationStore);
        }

        /// <summary>
        /// The host logger when the container has one. Раньше файловые сторы получали <c>null</c>, и КАЖДЫЙ
        /// сбой записи памяти или переписки в бою исчезал бесследно: строки в логе не было даже там, где код
        /// её «писал».
        /// </summary>
        private static ILog ResolveLogOrNull(IObjectResolver c)
        {
            return c.TryResolve(out ILog log) ? log : null;
        }

        private static void RegisterInMemoryConversationSummaryStore(IContainerBuilder builder)
        {
            builder.Register<InMemoryConversationSummaryStore>(Lifetime.Singleton).AsSelf();
            builder.Register<IConversationSummaryStore>(c =>
                    new ScopedConversationSummaryStoreDecorator(
                        c.Resolve<InMemoryConversationSummaryStore>(),
                        c.Resolve<IAgentMemoryScopeProvider>()),
                Lifetime.Singleton);
        }

        /// <summary>
        /// Registers a host provider before <see cref="CorePortableInstaller.RegisterCorePortable"/> adds its
        /// backward-compatible empty default. A non-null host instance therefore wins deterministically.
        /// </summary>
        internal static void RegisterAgentMemoryScopeProvider(
            IContainerBuilder builder,
            IAgentMemoryScopeProvider provider)
        {
            if (provider != null)
            {
                builder.RegisterInstance<IAgentMemoryScopeProvider>(provider);
            }
        }

        /// <summary>
        /// Registers one file or in-memory backing according to <paramref name="mode"/>. The public
        /// <see cref="IAgentMemoryStore"/> and <see cref="IConversationTranscriptStore"/> resolve to dedicated
        /// scoped decorators; the selected store remains their shared private backing.
        /// Called from <see cref="Configure"/>; internal for EditMode DI tests.
        /// </summary>
        /// <param name="builder">Container builder.</param>
        /// <param name="mode">Backing selection.</param>
        /// <param name="maxChatHistoryMessages">Потолок сообщений чата на роль (см. <see cref="SetConversationHistoryCaps"/>).</param>
        /// <param name="maxTranscriptEntries">Потолок строк транскрипта на роль.</param>
        internal static void RegisterAgentMemoryStore(
            IContainerBuilder builder,
            AgentMemoryPersistenceMode mode = AgentMemoryPersistenceMode.Persistent,
            int maxChatHistoryMessages = FileAgentMemoryStore.DefaultMaxChatHistoryMessages,
            int maxTranscriptEntries = FileAgentMemoryStore.DefaultMaxTranscriptEntries)
        {
            if (mode == AgentMemoryPersistenceMode.Persistent)
            {
                // WHY: Lambda registration: the ctor's optional string rootDirectory must not be injected.
                builder.Register(c => new FileAgentMemoryStore(
                            ResolveLogOrNull(c),
                            maxChatHistoryMessages: maxChatHistoryMessages,
                            maxTranscriptEntries: maxTranscriptEntries),
                        Lifetime.Singleton)
                    .AsSelf();
                builder.Register<ScopedAgentMemoryStoreDecorator>(c =>
                            new ScopedAgentMemoryStoreDecorator(
                                c.Resolve<FileAgentMemoryStore>(),
                                c.Resolve<IAgentMemoryScopeProvider>(),
                                ResolveConversationSummaryStoreOrNull(c)),
                        Lifetime.Singleton)
                    .As<IAgentMemoryStore>();
                builder.Register<IConversationTranscriptStore>(c =>
                        new ScopedConversationTranscriptStoreDecorator(
                            c.Resolve<FileAgentMemoryStore>(),
                            c.Resolve<IAgentMemoryScopeProvider>()),
                    Lifetime.Singleton);
                return;
            }

            if (mode == AgentMemoryPersistenceMode.SessionOnly)
            {
                // WHY: Lambda registration: VContainer otherwise tries to inject the optional integer caps.
                builder.Register(_ => new InMemoryAgentMemoryStore(
                        maxChatHistoryMessages,
                        maxTranscriptEntries), Lifetime.Singleton)
                    .AsSelf();
                builder.Register<ScopedAgentMemoryStoreDecorator>(c =>
                            new ScopedAgentMemoryStoreDecorator(
                                c.Resolve<InMemoryAgentMemoryStore>(),
                                c.Resolve<IAgentMemoryScopeProvider>(),
                                ResolveConversationSummaryStoreOrNull(c)),
                        Lifetime.Singleton)
                    .As<IAgentMemoryStore>();
                builder.Register<IConversationTranscriptStore>(c =>
                        new ScopedConversationTranscriptStoreDecorator(
                            c.Resolve<InMemoryAgentMemoryStore>(),
                            c.Resolve<IAgentMemoryScopeProvider>()),
                    Lifetime.Singleton);
                return;
            }

            throw new System.ArgumentOutOfRangeException(nameof(mode), mode,
                "Unknown agent memory persistence mode.");
        }

        /// <summary>
        /// The scoped <see cref="IConversationSummaryStore"/> of this scope, so that
        /// <see cref="IAgentMemoryStore.ClearChatHistory"/> also drops the summary derived from the erased
        /// turns. <c>null</c> when the container has no summary store (EditMode containers that register the
        /// memory store alone): a missing summary is nothing to clear, not a reason to fail every resolve.
        /// </summary>
        private static IConversationSummaryStore ResolveConversationSummaryStoreOrNull(IObjectResolver c)
        {
            return c.TryResolve(out IConversationSummaryStore summaries) ? summaries : null;
        }
    }
}
