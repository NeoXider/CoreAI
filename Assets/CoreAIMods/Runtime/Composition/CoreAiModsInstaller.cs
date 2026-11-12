using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Mods.WorldPackages;
using CoreAI.Scripting;
using CoreAI.Sandbox.LuaCs;
using Newtonsoft.Json;
using UnityEngine;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Registers the entire Lua mod subsystem into a DI container. This is the inverse of the
    /// dependency: the core (<see cref="WorldCommandsInstaller.RegisterWorldCommands"/>) registers only
    /// the native world-command executors, while this module installer — living in the
    /// <c>com.neoxider.coreaimods</c> package — adds the Lua-CSharp mod stack (sandbox + gameplay
    /// bindings + persistent runtime + one-off executor) and registers the <c>execute_lua</c> +
    /// <c>manage_mods</c> + <c>get_mod_logs</c> + world-package tools (and the Lua Modding, Rbx API and Full
    /// Lua skills) into the <see cref="BuiltInToolProfileIds.LuaBuilder"/> tool profile, which the built-in
    /// Programmer role definition references.
    /// <para>
    /// The active VM is Lua-CSharp (<see cref="LuaCsModRuntime"/>): a managed, AOT-safe runtime that
    /// works on IL2CPP/WebGL. Consumers reach it through the VM-agnostic <see cref="ILuaModRuntime"/>
    /// (manage_mods, auto-repair) and <see cref="LuaTool.ILuaExecutor"/> (execute_lua) seams.
    /// </para>
    /// Call it on the same container that already ran <see cref="WorldCommandsInstaller.RegisterWorldCommands"/>
    /// (it resolves the world command sink and prefab whitelist from there). In a scene the idiomatic
    /// wiring is a child <see cref="CoreAiModsLifetimeScope"/> parented to the CoreAI scope; tests call
    /// this extension directly on their builder.
    /// </summary>
    public static class CoreAiModsInstaller
    {
        /// <param name="builder">Container the core world commands were already registered on.</param>
        /// <param name="allowedLuaScenes">Scene whitelist for the Lua <c>coreai_world_load_scene</c> binding.</param>
        /// <param name="enableFullLuaAccess">
        /// When true, mods loaded through this composition get the Full tier (reflection over arbitrary
        /// GameObjects/components). Host/singleplayer only — never grant to a networked client.
        /// </param>
        /// <param name="enableFullLuaPrivateAccess">
        /// When true, Full-tier Lua reflection may access non-public members. Off by default.
        /// </param>
        /// <param name="fullLuaBlacklistPolicy">Optional deny-list applied to Full-tier reflection.</param>
        /// <param name="modStoreId">
        /// Optional namespace for the default file-backed mod stores. Empty (the default) keeps the
        /// shared store location the main game uses today; non-empty isolates this composition's
        /// persisted mods under its own subdirectory so compositions with different capability tiers
        /// (e.g. demo scenes) never rehydrate each other's mods. Ignored when the host registers its
        /// own <see cref="ILuaModStore"/>/<see cref="ILuaModSourceStore"/>.
        /// </param>
        /// <param name="worldAclVersion">
        /// ACL schema assigned to a newly composed Rbx world. Pass null only when opening a legacy
        /// world whose persisted metadata has no ACL version.
        /// </param>
        /// <param name="applicationIsPlayingProvider">
        /// Optional host seam for determining whether startup should create the runtime ticker. Null uses
        /// <see cref="Application.isPlaying"/>; headless composition tests provide a false-returning delegate.
        /// </param>
        /// <param name="skillTextProvider">
        /// Optional host seam for loading a Resources skill override by path. Null uses
        /// <see cref="Resources.Load{T}(string)"/>; headless composition tests provide a null-returning delegate.
        /// </param>
        /// <param name="worldSessionSourceStore">
        /// Optional transactional backing store used by initial load, world-package replacement, capture,
        /// runtime and Hub through the stable active-session facade. It must implement
        /// <see cref="IRbxWorldModSourceStore"/>; null creates the default file-backed store.
        /// </param>
        /// <param name="worldPackageStore">
        /// Optional world-package store for manual slots, autosaves and, when it implements
        /// <see cref="IRbxWorldStartupStore"/>, the world that opens on the next start. Null creates the
        /// default file-backed store under <c>CoreAiStorageRoot</c> whose startup area is namespaced by
        /// <paramref name="modStoreId"/>; composition tests pass a temporary or in-memory store.
        /// </param>
        public static void RegisterCoreAiMods(
            this IContainerBuilder builder,
            System.Collections.Generic.IEnumerable<string> allowedLuaScenes = null,
            bool enableFullLuaAccess = false,
            bool enableFullLuaPrivateAccess = false,
            IFullLuaAccessBlacklistPolicy fullLuaBlacklistPolicy = null,
            string modStoreId = null,
            int? worldAclVersion = InstanceRegistry.CurrentWorldAclVersion,
            System.Func<bool> applicationIsPlayingProvider = null,
            System.Func<string, string> skillTextProvider = null,
            ILuaModSourceStore worldSessionSourceStore = null,
            IRbxWorldPackageStore worldPackageStore = null)
        {
            RegisterCoreAiMods(builder, allowedLuaScenes, enableFullLuaAccess,
                enableFullLuaPrivateAccess, fullLuaBlacklistPolicy, modStoreId, worldAclVersion,
                applicationIsPlayingProvider, skillTextProvider, worldSessionSourceStore, worldPackageStore, null);
        }

        /// <summary>Registers scoped host gameplay additions without changing the existing overload or grants.</summary>
        public static void RegisterCoreAiMods(
            this IContainerBuilder builder,
            System.Collections.Generic.IEnumerable<string> allowedLuaScenes,
            bool enableFullLuaAccess,
            bool enableFullLuaPrivateAccess,
            IFullLuaAccessBlacklistPolicy fullLuaBlacklistPolicy,
            string modStoreId,
            int? worldAclVersion,
            System.Func<bool> applicationIsPlayingProvider,
            System.Func<string, string> skillTextProvider,
            ILuaModSourceStore worldSessionSourceStore,
            IRbxWorldPackageStore worldPackageStore,
            System.Action<LuaCsApiRegistry, LuaCapabilities> additionalGameplayBindings)
        {
            LuaCapabilities scriptCapabilities = enableFullLuaAccess
                ? LuaCapabilities.All | LuaCapabilities.Full
                : LuaCapabilities.All;
            IActorIdentityProvider fallbackHostIdentityProvider =
                CoreServicesInstaller.DefaultLocalHostIdentityProvider;

            // One-off execute_lua calls run untrusted, ad-hoc snippets rather than an installed mod, so
            // Full-tier reflection is stripped from them by default even when persistent mods are granted
            // it; a host must opt in explicitly since a single malformed prompt-authored snippet has no
            // review step the way loading a named mod does.
            LuaCapabilities oneOffCapabilities = enableFullLuaAccess
                ? scriptCapabilities
                : scriptCapabilities & ~LuaCapabilities.Full;

            // Persistent stores so mods survive a restart and export/import works. ResolveOrDefault in the
            // factory means a host that wires its own store wins; otherwise these file-backed defaults apply.
            builder.Register(_ => new FileLuaModStore(storeId: modStoreId), Lifetime.Singleton).As<ILuaModStore>();
            // WHY one backend per composition: every world of this game shares one DataStore namespace, and
            // disposing the scope writes whatever is still pending.
            if (!builder.Exists(typeof(CoreAI.Mods.Rbx.Instances.DataStores.RbxDataStoreBackend)))
            {
                builder.Register(
                    c => CoreAiDataStoreDefaults.CreateBackend(
                        modStoreId,
                        c.ResolveOrDefault<Logging.ILog>(),
                        c.ResolveOrDefault<CoreAI.Mods.Rbx.Instances.DataStores.RbxDataStoreOptions>()),
                    Lifetime.Singleton);
            }

            if (worldSessionSourceStore != null)
            {
                if (worldSessionSourceStore is not IRbxWorldModSourceStore)
                {
                    throw new System.ArgumentException(
                        "The world-session source store must support transactional exact replacement.",
                        nameof(worldSessionSourceStore));
                }

                builder.RegisterInstance(
                    new WorldSessionSourceBackend(worldSessionSourceStore));
            }
            else
            {
                builder.Register(
                        _ => new FileLuaModSourceStore(storeId: modStoreId),
                        Lifetime.Singleton)
                    .AsSelf();
                builder.Register(
                    c => new WorldSessionSourceBackend(c.Resolve<FileLuaModSourceStore>()),
                    Lifetime.Singleton);
            }
            if (worldPackageStore != null)
            {
                builder.RegisterInstance(worldPackageStore).As<IRbxWorldPackageStore>();
            }
            else
            {
                // WHY the startup area is namespaced by the mods store id: a world selected in one
                // composition must never open in another whose Lua tier and mod set differ.
                builder.Register(
                        _ => new FileRbxWorldPackageStore(startupNamespace: modStoreId),
                        Lifetime.Singleton)
                    .As<IRbxWorldPackageStore>();
            }
            builder.Register(c => new ConfirmedWorldMutationGate(
                    cancellationToken =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        IRbxWorldRuntimeService worldRuntimeService =
                            c.Resolve<IRbxWorldRuntimeService>();
                        return Cysharp.Threading.Tasks.UniTask.FromResult(
                            worldRuntimeService.CaptureCurrent());
                    },
                    c.Resolve<IRbxWorldPackageStore>()),
                Lifetime.Singleton)
                .AsSelf()
                .As<IConfirmedWorldMutationGate>();

            // Mods shipped with the build: Resources/CoreAIMods/*.lua are seeded into the store on startup
            // (before rehydrate) so a game can ship with a ready-made set of mods. Hosts register additional
            // IBundledModSource entries (StreamingAssets, Addressables, remote) to extend the set.
            builder.Register<IBundledModSource>(_ => new ResourcesBundledModSource(), Lifetime.Singleton);

            // WHY one list per mod store, shared by manage_mods, the Hub Mods page and the world session: a folder
            // made in any of them shows in the others and travels in the world package. A host store that is not
            // file-backed gets one in-memory list for this composition.
            if (!builder.Exists(typeof(Mods.Folders.IModFolderListStore), true))
            {
                builder.Register<Mods.Folders.IModFolderListStore>(
                    c => c.Resolve<WorldSessionSourceBackend>().Store is FileLuaModSourceStore files
                        ? FileModFolderListStore.For(files)
                        : new Mods.Folders.InMemoryModFolderListStore(),
                    Lifetime.Singleton);
            }

            // WHY: one mod log ring buffer per container backs get_mod_logs (LLM tool + MCP tool); it is
            // consumed via ResolveOrDefault at the stack factory below, so a host that registers its own
            // ILuaLogService wins — the same override story as the mod stores. No mirrorLogger: the mod
            // runtime already writes errors to the scoped log unconditionally, and in production the
            // mirror would resolve to the same game-log sink and duplicate every error entry.
            builder.Register(c => new LuaLogService(), Lifetime.Singleton).As<ILuaLogService>();

            // WHY a container singleton built eagerly: the bridge mirrors the CoreAI scope's UI focus
            // (chat panel, Hub) into the scene world's input focus, so typing in CoreAI UI reaches Lua as
            // gameProcessedEvent = true; the container disposes it with the scope, releasing the claim.
            // Without a world host or a UI focus service it is inert, and RbxWorldHost.InputFocus stays the
            // host-side path for a game's own UI.
            builder.Register(c => new CoreAiUiFocusBridge(
                    c.ResolveOrDefault<CoreAI.UiFocus.ICoreAiUiFocus>(),
                    c.ResolveOrDefault<RbxWorldHost>()?.InputFocus),
                Lifetime.Singleton);
            builder.RegisterBuildCallback(container => container.Resolve<CoreAiUiFocusBridge>());
            builder.RegisterRuntimeUiLua();
            RegisterWorldAgents(builder);
            RegisterWorldChatHost(builder);

            // WHY: one Lua-CSharp stack shared across both surfaces, so persistent runtime and one-off executor
            // resolve the same sandbox + gameplay bindings instance rather than diverging copies.
            LuaCsRbxApiBindings[] rbxApiHolder = new LuaCsRbxApiBindings[1];
            builder.Register(c =>
            {
                // WHY: a scene that references a RbxWorldHost gets a VISIBLE Rbx world — its registry/
                // game/binder back the Lua surface, so Instance.new('Part') materializes a GameObject.
                // Without a host the world stays headless in-memory (same API, no rendering).
                // Initialize() is idempotent and safe before Awake ordering.
                Mods.Rbx.Binding.RbxWorldHost rbxHost = c.ResolveOrDefault<Mods.Rbx.Binding.RbxWorldHost>();
                // WHY: the scoped ILog applies the authored GameLogSettings filter; Log.Instance backs
                // minimal containers (tests, headless tools) that never registered one, so the Rbx
                // wiring diagnostics below survive either way.
                Logging.ILog rbxLog = c.ResolveOrDefault<Logging.ILog>() ?? Logging.Log.Instance;
                IRbxRuntimeObservabilitySink observability =
                    c.ResolveOrDefault<IRbxRuntimeObservabilitySink>()
                    ?? NullRbxRuntimeObservabilitySink.Instance;
                INetworkBridge networkBridge = c.ResolveOrDefault<INetworkBridge>()
                    ?? new NullNetworkBridge();
                // WHY resolved once here and threaded everywhere below: a game overrides the coroutine
                // resume budget the same way it overrides the character motor — an optional serialized
                // field on CoreAiModsLifetimeScope registering into the container. The SAME instance must
                // reach every coroutine-handle construction site in this world (the scheduler thread
                // factory's signal-runner pool, its own IScriptEngine, and the mod-stack's engine) so a
                // later ScriptContext:SetTimeout call — which mutates this one object — is visible from
                // all of them, not just whichever site happened to resolve it first.
                Sandbox.LuaCs.LuaCsCoroutineBudgetSettings coroutineResumeBudget =
                    c.ResolveOrDefault<Sandbox.LuaCs.LuaCsCoroutineBudgetSettings>();
                LuaCsRbxApiBindings rbxApi;
                if (rbxHost != null)
                {
                    rbxHost.SetLog(rbxLog);
                    rbxHost.Initialize();
                    rbxHost.Registry.ConfigureWorldAclVersion(worldAclVersion);
                    rbxLog.Info(
                        $"[CoreAiMods] RbxWorldHost resolved — registry has {rbxHost.Registry.Count} instances, " +
                        $"binder bound count={rbxHost.Binder.BoundCount}.",
                        Logging.LogTag.World);
                    rbxApi = new LuaCsRbxApiBindings(
                        rbxHost.Registry, rbxHost.Game, partSink: rbxHost.Binder,
                        cameraRig: rbxHost.CameraRig, inputSource: rbxHost.InputSource,
                        pickSource: rbxHost.PickSource,
                        observability: observability,
                        networkBridge: networkBridge,
                        log: msg => rbxLog.Warn(
                            "[CoreAI.RbxApi] " + msg, Logging.LogTag.World),
                        coroutineResumeBudget: coroutineResumeBudget);
                }
                else
                {
                    InstanceRegistry registry = new(worldAclVersion: worldAclVersion);
                    RbxDataModel game = DataModelBootstrap.CreateGame(registry);
                    rbxLog.Error(
                        "[CoreAiMods] RbxWorldHost NOT resolved — mods run headless. " +
                        "Instance.new / workspace mutations produce no GameObjects. " +
                        "Check: (1) RbxWorldHost component exists in the scene, " +
                        "(2) CoreAiModsLifetimeScope.robloxWorldHost is wired to it, " +
                        "(3) link.xml preserves CoreAI.RbxApi.Binding assembly.",
                        Logging.LogTag.World);
                    rbxApi = new LuaCsRbxApiBindings(
                        registry: registry,
                        game: game,
                        observability: observability,
                        networkBridge: networkBridge,
                        log: msg => rbxLog.Warn("[CoreAI.RbxApi] " + msg, Logging.LogTag.World),
                        coroutineResumeBudget: coroutineResumeBudget);
                }

                if (rbxHost != null)
                {
                    // WHY wired here and nowhere else: this is the only place that holds both the
                    // Rbx API and the binder that owns the backing GameObjects, and without it every
                    // Humanoid in a real scene silently got the null motor — Position stuck at
                    // zero, so MoveTo reported arrival instantly and WalkSpeed moved nothing.
                    InstanceGameObjectBinder binder = rbxHost.Binder;
                    // WHY resolved here and passed down: the host registers its provider in the
                    // same container as everything else, and this is the only place that also
                    // holds the binder and the world host the provider needs to be given.
                    Mods.Rbx.Binding.IRbxCharacterMotorProvider motorProvider =
                        c.ResolveOrDefault<Mods.Rbx.Binding.IRbxCharacterMotorProvider>();
                    rbxApi.AttachCharacterMotorFactory(humanoid =>
                        CreateCharacterMotor(binder, humanoid, rbxHost, motorProvider));
                }

                rbxApiHolder[0] = rbxApi;
                AttachWorldServices(c, rbxApi);

                LuaCsModStack luaCsStack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = c.Resolve<IGameLogger>(),
                    LuaScriptVersions = c.ResolveOrDefault<ILuaScriptVersionStore>(),
                    DataOverlayVersions = c.ResolveOrDefault<IDataOverlayVersionStore>(),
                    CommandSink = c.Resolve<IAiGameCommandSink>(),
                    PrefabRegistry = c.ResolveOrDefault<ICoreAiPrefabRegistry>(),
                    AllowedScenes = allowedLuaScenes,
                    FullBlacklistPolicy = fullLuaBlacklistPolicy,
                    AllowNonPublicFullMembers = enableFullLuaPrivateAccess,
                    ModStore = c.ResolveOrDefault<ILuaModStore>(),
                    ModSourceStore = c.Resolve<WorldSessionSourceBackend>().Store,
                    Log = c.ResolveOrDefault<Logging.ILog>(),
                    LogService = c.ResolveOrDefault<ILuaLogService>(),
                    ExecutionObserver = c.ResolveOrDefault<ILuaExecutionObserver>(),
                    Observability = observability,
                    WorldMutationGate = c.Resolve<IConfirmedWorldMutationGate>(),
                    // WHY rbxApi.CoroutineResumeBudget and not re-resolving from the container: rbxApi
                    // above already settled on ONE instance (composition override or a fresh default);
                    // reusing it keeps this stack's own IScriptEngine reading the exact same live object
                    // instead of a second, independent default that ScriptContext:SetTimeout would miss.
                    CoroutineResumeBudget = rbxApi.CoroutineResumeBudget,
                    // WHY: demos and host self-tests call the plain execute seam; in an ACL-versioned world
                    // that call still needs a server-generated envelope, issued for the trusted host actor.
                    LocalActorResolver = () =>
                        (c.ResolveOrDefault<IActorIdentityProvider>() ?? fallbackHostIdentityProvider)
                        .GetActorContext(BuiltInAgentRoleIds.Programmer),
                    Capabilities = scriptCapabilities,
                    OneOffCapabilities = oneOffCapabilities,
                    AdditionalGameplayBindings = ComposeGameplayBindings(c, rbxApi, additionalGameplayBindings),
                    // WHY: a one-shot chunk may run for seconds under its wall-clock budget; on a
                    // single-threaded player that would freeze the whole page (measured: ~6 s on WebGL).
                    // The player-loop yielder lets the guard hand a frame back while the chunk runs.
                    FrameYielder = PlayerLoopScriptFrameYielder.Instance,
                    // WHY: one shared Roblox world too — persistent mods and one-off execute_lua resolve
                    // the same InstanceRegistry/game/workspace, so an instance a mod creates is the one the
                    // console navigates (roadmap §5.1.3). Opt-in per stack; wired here for production.
                    RbxApi = rbxApi,
                    // WHY: the Programmer builds worlds via the Rbx surface and Lua mechanics, not the
                    // low-level coreai_world_* primitives; the WorldEdit capability itself stays granted
                    // because Instance.new requires it.
                    RegisterWorldEditBuildBindings = false
                });

                // WHY no ModTearingDown teardown here: LuaCsModRuntimeFactory.Create wires the release of
                // a mod run's threads, connections and (on unload) instances into every stack it builds
                // with an Rbx API, this one and every session stack alike, and runs it after all
                // ModTearingDown subscribers.
                return new InitialLuaWorldSession(
                    luaCsStack,
                    rbxApi,
                    c.Resolve<WorldSessionSourceBackend>().Store);
            }, Lifetime.Singleton);

            builder.RegisterDisposeCallback(_ =>
            {
                rbxApiHolder[0]?.Dispose();
                rbxApiHolder[0] = null;
            });

            builder.Register(c =>
            {
                InitialLuaWorldSession initial = c.Resolve<InitialLuaWorldSession>();
                Mods.Rbx.Binding.RbxWorldHost sceneHost =
                    c.ResolveOrDefault<Mods.Rbx.Binding.RbxWorldHost>();
                Mods.Rbx.Binding.IRbxCharacterMotorProvider stagedMotorProvider =
                    c.ResolveOrDefault<Mods.Rbx.Binding.IRbxCharacterMotorProvider>();
                IRbxWorldSessionHost sessionHost = sceneHost != null
                    ? new RbxWorldSessionHostAdapter(sceneHost)
                    : new HeadlessRbxWorldSessionHost(
                        initial.RbxApi.Registry,
                        initial.RbxApi.Game,
                        partSink: initial.RbxApi.PartSink,
                        cameraRig: initial.RbxApi.CameraRig);
                INetworkBridge networkBridge = c.ResolveOrDefault<INetworkBridge>()
                    ?? new NullNetworkBridge();
                System.Func<IRbxWorldSessionCandidate, INetworkBridge, LuaCsRbxApiBindings>
                    rbxApiFactory = (candidate, stagedNetwork) =>
                    {
                        LuaCsRbxApiBindings stagedApi = new LuaCsRbxApiBindings(
                            candidate.Registry,
                            candidate.Game,
                            partSink: candidate.PartSink,
                            cameraRig: candidate.CameraRig,
                            inputSource: candidate.InputSource,
                            pickSource: candidate.PickSource,
                            observability: c.ResolveOrDefault<IRbxRuntimeObservabilitySink>()
                                ?? NullRbxRuntimeObservabilitySink.Instance,
                            networkBridge: stagedNetwork,
                            log: message => (c.ResolveOrDefault<Logging.ILog>()
                                ?? Logging.Log.Instance).Warn(
                                    "[CoreAI.RbxApi] " + message,
                                    Logging.LogTag.World),
                            // WHY the initial session's instance, not a fresh resolve: this composition
                            // has ONE coroutine resume budget policy, live-mutable through
                            // ScriptContext:SetTimeout; a world reload keeps it rather than quietly
                            // reverting a host-configured wall-clock override back to the baseline.
                            coroutineResumeBudget: initial.RbxApi.CoroutineResumeBudget);
                        AttachWorldServices(c, stagedApi);
                        // WHY here and not in AttachWorldServices: the initial world is built while the session
                        // controller is still being constructed, and the agents resolve the world through it; that
                        // world is attached once the container built (RegisterWorldAgents).
                        c.ResolveOrDefault<CoreAiWorldAgents>()?.AttachWorld(stagedApi.Scheduler);

                        // WHY the replacement world needs this too: a staged API starts with no motor
                        // factory, so without this a world loaded at runtime gives every Humanoid the
                        // motor that never moves. The initial session got one at composition; the
                        // replacement was silently left without it. The staged binder is safe to
                        // capture here — unlike the physics port below, it is never disposed; Commit
                        // reparents the staging root instead of replacing the binder.
                        if (candidate.PartSink is InstanceGameObjectBinder stagedBinder)
                        {
                            stagedApi.AttachCharacterMotorFactory(
                                humanoid => CreateCharacterMotor(
                                    stagedBinder, humanoid, sceneHost, stagedMotorProvider));
                        }

                        // WHY the physics port is NOT attached here: at Stage time the only port that
                        // exists is the LIVE one still serving the outgoing world.
                        // RbxWorldSessionHostAdapter.Commit -> RbxWorldHost.PublishReplacement disposes
                        // that port and builds a fresh one over the newly published binder, so
                        // attaching it here would either wire the staged WorldPhysics to a port about
                        // to be disposed (Dispose only clears contact callbacks; it never throws, so
                        // Raycast would silently start missing and Touched/TouchEnded would silently
                        // stop firing once Commit runs) or, before that, relay contacts carrying
                        // instance ids from the OUTGOING world into the staged one. The correct port
                        // does not exist until after Commit, so RbxWorldRuntimeSessionController
                        // attaches it there instead, once IRbxWorldSessionHost.PhysicsPort reflects the
                        // newly published port. Until then the staged WorldPhysics answers through the
                        // null port, same as any other pre-publish state.
                        return stagedApi;
                    };
                System.Func<LuaCsRbxApiBindings, ILuaModSourceStore, ILuaModStore,
                    ILuaScriptVersionStore, LuaCsModStack>
                    stackFactory = (rbxApi, sourceStore, modStore, versionStore) => CreateSessionStack(
                        c,
                        rbxApi,
                        sourceStore,
                        modStore,
                        versionStore,
                        allowedLuaScenes,
                        fullLuaBlacklistPolicy,
                        enableFullLuaPrivateAccess,
                        scriptCapabilities,
                        oneOffCapabilities,
                        additionalGameplayBindings);
                RbxWorldRuntimeSessionController controller = new RbxWorldRuntimeSessionController(
                    sessionHost,
                    c.Resolve<IRbxWorldPackageStore>(),
                    initial.SourceStore,
                    initial.Stack,
                    initial.RbxApi,
                    rbxApiFactory,
                    stackFactory,
                    WireSessionTeardown,
                    networkBridge,
                    scriptCapabilities,
                    (scriptCapabilities & LuaCapabilities.Full) != 0,
                    c.ResolveOrDefault<ILuaModStore>(),
                    c.ResolveOrDefault<ILuaScriptVersionStore>(),
                    message => (c.ResolveOrDefault<Logging.ILog>()
                        ?? Logging.Log.Instance).Warn(
                            "[CoreAI.WorldLoad] " + message,
                            Logging.LogTag.World));
                // WHY set before anything captures: the world package carries the folders, including empty ones.
                controller.ModFolderListStore = c.ResolveOrDefault<Mods.Folders.IModFolderListStore>();
                return controller;
            }, Lifetime.Singleton).AsSelf().As<IRbxWorldRuntimeService>().As<IRbxWorldStartupSelection>();

            builder.Register(c => c.Resolve<RbxWorldRuntimeSessionController>().Stack, Lifetime.Singleton)
                .AsSelf();
            builder.Register(c => c.Resolve<RbxWorldRuntimeSessionController>().Runtime, Lifetime.Singleton)
                .As<ILuaModRuntime>();
            builder.Register(c => c.Resolve<RbxWorldRuntimeSessionController>().Executor, Lifetime.Singleton)
                .As<LuaTool.ILuaExecutor>();
            builder.Register(c => c.Resolve<RbxWorldRuntimeSessionController>().LogicSlots, Lifetime.Singleton)
                .AsSelf();
            builder.Register(c => c.Resolve<RbxWorldRuntimeSessionController>().SourceStore, Lifetime.Singleton)
                .As<ILuaModSourceStore>();

            // Startup rehydration + frame ticker (play mode only). EditMode containers share the REAL
            // persistent store, so rehydrating there would inject earlier-run mods into every fresh
            // container; and DontDestroyOnLoad throws outside play mode. EditMode consumers load and Tick
            // explicitly. Best-effort: a rehydrate failure never aborts container construction.
            // WHY: the ticker is DontDestroyOnLoad (must keep ticking across additive scene loads), so
            // scene unload does NOT destroy it — without this explicit dispose hook every scope build
            // leaks an immortal ticker that drives persisted mod handlers into scenes/tests that no
            // longer own them (observed: cross-test world-command spam, eventual editor OOM in PlayMode).
            GameObject[] tickerHolder = new GameObject[1];
            builder.RegisterDisposeCallback(_ =>
            {
                if (tickerHolder[0] != null)
                {
                    // WHY both: a scope disposed after Play Mode ended (or by an editor tool) must not call
                    // Object.Destroy, which edit mode refuses with an error log.
                    if (Application.isPlaying)
                    {
                        Object.Destroy(tickerHolder[0]);
                    }
                    else
                    {
                        Object.DestroyImmediate(tickerHolder[0]);
                    }

                    tickerHolder[0] = null;
                }
            });

            // WHY: The explicit non-playing seam must not JIT a callback body containing Unity ECalls.
            bool skipRuntimeStartupCallback = applicationIsPlayingProvider != null &&
                                              !applicationIsPlayingProvider();
            if (!skipRuntimeStartupCallback)
            {
                builder.RegisterBuildCallback(container =>
                {
                    try
                    {
                        Logging.Log.Instance.Info(
                            $"[CoreAI] CoreAiMods (Lua-CSharp): capability grant = {scriptCapabilities} " +
                            $"(enableFullLuaAccess={enableFullLuaAccess})");

                    bool applicationIsPlaying = applicationIsPlayingProvider != null
                        ? applicationIsPlayingProvider()
                        : Application.isPlaying;
                    if (applicationIsPlaying)
                    {
                        LuaCsModStack stack = container.Resolve<LuaCsModStack>();
                        RbxWorldRuntimeSessionController sessionController =
                            container.Resolve<RbxWorldRuntimeSessionController>();

                        GameObject tickerGo = new("CoreAI_LuaModTicker");
                        tickerHolder[0] = tickerGo;
                        Object.DontDestroyOnLoad(tickerGo);

                        async Cysharp.Threading.Tasks.UniTaskVoid RestoreRehydrateAndStartTickingAsync()
                        {
                            // WHY seeded into the default store and before the restore: a start that
                            // falls back to the default world rehydrates shipped mods exactly like
                            // persisted ones, and the restored world's exact source set never sees them.
                            // Best-effort: a seeding failure never blocks the startup.
                            System.Collections.Generic.List<IBundledModSource> bundledSources = new();
                            try
                            {
                                System.Collections.Generic.IEnumerable<IBundledModSource> bundled =
                                    container.Resolve<System.Collections.Generic.IEnumerable<IBundledModSource>>();
                                bundledSources.AddRange(bundled);
                                BundledModSeeder.SeedResult seeded = new BundledModSeeder(
                                    container.Resolve<WorldSessionSourceBackend>().Store,
                                    new System.Collections.Generic.List<IBundledModSource>(bundledSources),
                                    container.ResolveOrDefault<Logging.ILog>()).Seed();
                                // WHY: without a registered ILog the seeder's own error goes nowhere, and a world
                                // with no bundled mods looks like a silent empty start.
                                if (seeded.Failed > 0)
                                {
                                    Logging.Log.Instance.Error(
                                        "[CoreAI] Bundled mods were not installed: " + seeded);
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Logging.Log.Instance.Warn($"[CoreAI] Bundled mod seeding failed: {ex.Message}");
                            }

                            IActorIdentityProvider hostIdentityProvider =
                                container.ResolveOrDefault<IActorIdentityProvider>() ??
                                fallbackHostIdentityProvider;
                            ActorContext hostActor = hostIdentityProvider.GetActorContext(
                                BuiltInAgentRoleIds.Programmer);
                            Logging.ILog startupLog =
                                container.ResolveOrDefault<Logging.ILog>() ?? Logging.Log.Instance;
                            CoreAiWorldKeepOptions keep = container.ResolveOrDefault<CoreAiWorldKeepOptions>();
                            if (keep != null && keep.KeepWorldBetweenStarts)
                            {
                                sessionController.KeepWorldBetweenStarts(bundledSources);
                            }

                            RbxWorldStartupRestoreOutcome startupOutcome = await RbxWorldStartupSequence.RunAsync(
                                sessionController,
                                () =>
                                {
                                    BindPersistedActorAttribution(
                                        container.ResolveOrDefault<ILuaModSourceStore>(),
                                        stack.GameplayBindings.RbxApi?.Registry,
                                        hostActor);
                                    // WHY legacy placement: the default world's store holds no tree, so a declared
                                    // mod gets its script instance at its placement instead of starting detached.
                                    sessionController.CurrentConcreteRuntime.RehydrateFromStore(
                                        scriptCapabilities,
                                        (scriptCapabilities & LuaCapabilities.Full) != 0,
                                        legacyScriptPlacement: true);
                                },
                                message => startupLog.Warn(
                                    "[CoreAI.WorldLoad] " + message,
                                    Logging.LogTag.World));

                            // WHY: the scope can be disposed while the restore awaited (leaving play mode).
                            if (tickerGo == null)
                            {
                                return;
                            }

                            if (startupOutcome != RbxWorldStartupRestoreOutcome.Restored)
                            {
                                sessionController.AdoptDefaultWorldIfKept();
                            }

                            // WHY read only now: a restored startup world replaced the initial session, so
                            // the pumps below must bind the published world's API, not the default one.
                            LuaCsRbxApiBindings stackRbxApi = stack.GameplayBindings.RbxApi;

                            // WHY the driver gets the session controller and nothing else: one scaled
                            // frame is one ModScheduler.Advance on the PUBLISHED session. The scheduler
                            // walks the phase pipeline (Stepped -> delayed work -> input -> Heartbeat ->
                            // RenderStepped) and the bindings that own that scheduler fire each phase's
                            // signals, so the driver must carry no per-phase pumps of its own — a second
                            // route fires every RunService signal twice per frame.
                            LuaModRuntimeTickDriver tickDriver =
                                tickerGo.AddComponent<LuaModRuntimeTickDriver>();
                            tickDriver.Initialize(sessionController, hostActor);

                            // WHY wired here and not in RbxWorldHost: the host owns the scene objects,
                            // the mod stack owns the world-physics facade, and this is the one place
                            // that sees both. A scene with no RbxWorldHost keeps the null port, so a
                            // headless world still answers Raycast with a miss instead of throwing.
                            Mods.Rbx.Binding.RbxWorldHost physicsHost =
                                container.ResolveOrDefault<Mods.Rbx.Binding.RbxWorldHost>();
                            if (physicsHost?.PhysicsPort != null && stackRbxApi?.WorldPhysics != null)
                            {
                                stackRbxApi.WorldPhysics.AttachPort(physicsHost.PhysicsPort);
                                tickDriver.AttachPhysicsPumps(
                                    stackRbxApi.WorldPhysics.BeginPhysicsStep,
                                    physicsHost.PhysicsPort.ApplyGravity);
                            }

                            // WHY the motor pump is attached unconditionally, unlike the physics pumps
                            // above: a character motor is driven by the fixed step even in a world whose
                            // physics port is the null one, and a motor stepped from the render frame
                            // moves at a rate that depends on the frame rate. Leaving this unattached is
                            // how the motor came to exist without ever being driven.
                            if (stackRbxApi != null)
                            {
                                tickDriver.AttachCharacterMotorStep(stackRbxApi.StepCharacterMotors);
                            }

                            // WHY a resolver rather than the captured port: PublishReplacement
                            // disposes the old port and builds a new one over the new binder, so a
                            // captured ApplyGravity keeps pushing bodies of a world that no longer
                            // exists. The driver reads this every fixed step.
                            if (physicsHost != null)
                            {
                                tickDriver.AttachLiveGravityPump(
                                    () => physicsHost.PhysicsPort != null
                                        ? (System.Action)physicsHost.PhysicsPort.ApplyGravity
                                        : null);
                            }
                        }

                        // Ordering contract (audit finding W4, see WORLD_COMMANDS.md §7): mod rehydrate
                        // must run AFTER WorldStateEntryPoint's startup world restore completes, or a mod
                        // that re-spawns its own objects can double-spawn against the restored snapshot,
                        // or the snapshot's clean-slate destroy can remove what the mod just made. This
                        // callback runs at child-scope Awake time, before the parent scope's Start() phase
                        // (where the restore happens), so it cannot simply check the flag synchronously —
                        // it defers via WorldRestoreGate, which polls WorldRestoreCompleted with a 5s
                        // timeout fallback so a broken/absent world-state wiring never blocks mods forever.
                        IWorldStateManager worldState = container.ResolveOrDefault<IWorldStateManager>();
                        if (worldState != null)
                        {
                            tickerGo.AddComponent<WorldRestoreGate>().Begin(
                                worldState,
                                () => RestoreRehydrateAndStartTickingAsync().Forget());
                        }
                        else
                        {
                            RestoreRehydrateAndStartTickingAsync().Forget();
                        }
                    }
                    }
                    catch (VContainerException)
                    {
                        // Minimal containers (tests, headless tools) may omit the mod runtime; rehydration
                        // is an additive convenience, not a requirement.
                    }
                });
            }

            builder.RegisterBuildCallback(container =>
            {
                try
                {
                    AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();
                    ICoreAISettings settings = container.Resolve<ICoreAISettings>();
                    Logging.ILog log = container.Resolve<Logging.ILog>();
                    LuaGenerationRateLimiter limiter = container.Resolve<LuaGenerationRateLimiter>();
                    IActorIdentityProvider actorIdentityProvider =
                        container.ResolveOrDefault<IActorIdentityProvider>() ?? fallbackHostIdentityProvider;

                    // WHY the lua-builder profile instead of the Programmer role: which roles build with Lua is
                    // role-definition data (the shipped Programmer definition lists this profile). Each factory
                    // receives the role it serves, so the tool's actor identity follows that role. Services are
                    // resolved in the old order, so a container missing one still gets the tools before it.
                    const string luaBuilder = BuiltInToolProfileIds.LuaBuilder;
                    LuaTool.ILuaExecutor luaExecutor = container.Resolve<LuaTool.ILuaExecutor>();
                    policy.AddToolToProfile(luaBuilder, LuaTool.ExecuteLuaToolName, roleId => new LuaLlmTool(
                        luaExecutor,
                        settings,
                        log,
                        limiter,
                        actorIdentityProvider,
                        roleId));
                    ILuaModRuntime modRuntime = container.Resolve<ILuaModRuntime>();
                    IConfirmedWorldMutationGate worldMutationGate = container.Resolve<IConfirmedWorldMutationGate>();
                    Mods.Folders.IModFolderListStore folders =
                        container.ResolveOrDefault<Mods.Folders.IModFolderListStore>();
                    policy.AddToolToProfile(luaBuilder, "manage_mods", roleId => new LuaModsLlmTool(
                        modRuntime,
                        settings,
                        log,
                        scriptCapabilities,
                        allowModManagement: true,
                        actorIdentityProvider: actorIdentityProvider,
                        roleId: roleId,
                        worldMutationGate: worldMutationGate,
                        rateLimiter: limiter,
                        folderList: folders));
                    // WHY the marshaler: without a host scheduler the tool's wait_seconds answers at once.
                    // WHY the engine readers here and not a tool of their own: one extra argument costs the
                    // Programmer a few tokens per request, a separate diagnostics tool a whole definition.
                    policy.AddToolToProfile(luaBuilder,
                        new GetModLogsLlmTool(
                            container.Resolve<ILuaLogService>(),
                            container.ResolveOrDefault<ICoreAISettings>()?.ToolInvocationMarshaler,
                            container.ResolveOrDefault<CoreAiModSelfRepairEntryPoint>()?.Lifecycle,
                            container.ResolveOrDefault<CoreAI.Diagnostics.IRuntimeLogSource>(),
                            container.ResolveOrDefault<CoreAI.Diagnostics.IRuntimePerformanceSource>()));
                    IRbxWorldRuntimeService worldRuntime = container.Resolve<IRbxWorldRuntimeService>();
                    policy.AddToolToProfile(luaBuilder, "save_world",
                        roleId => new SaveWorldLlmTool(worldRuntime, actorIdentityProvider, roleId));
                    policy.AddToolToProfile(luaBuilder, "load_world",
                        roleId => new LoadWorldLlmTool(worldRuntime, actorIdentityProvider, roleId));
                    policy.AddToolToProfile(luaBuilder, "list_autosaves",
                        roleId => new ListAutoSavesLlmTool(worldRuntime, actorIdentityProvider, roleId));
                    policy.AddToolToProfile(luaBuilder, "load_autosave",
                        roleId => new LoadAutoSaveLlmTool(worldRuntime, actorIdentityProvider, roleId));

                    string skillOverride = skillTextProvider != null
                        ? skillTextProvider("AgentSkills/LuaModding")
                        : Resources.Load<TextAsset>("AgentSkills/LuaModding")?.text;
                    policy.AddSkillToProfile(luaBuilder, SkillSet.FromTextContent(
                        BuiltInLuaModdingSkillText.SkillName,
                        BuiltInLuaModdingSkillText.SkillDescription,
                        skillOverride ?? BuiltInLuaModdingSkillText.Instructions));

                    string rbxSkillOverride = skillTextProvider != null
                        ? skillTextProvider("AgentSkills/RbxApi")
                        : Resources.Load<TextAsset>("AgentSkills/RbxApi")?.text;
                    policy.AddSkillToProfile(luaBuilder, SkillSet.FromTextContent(
                        BuiltInRbxApiSkillText.SkillName,
                        BuiltInRbxApiSkillText.SkillDescription,
                        rbxSkillOverride ?? BuiltInRbxApiSkillText.Instructions));

                    string fullLuaSkillOverride = skillTextProvider != null
                        ? skillTextProvider("AgentSkills/FullLua")
                        : Resources.Load<TextAsset>("AgentSkills/FullLua")?.text;
                    policy.AddSkillToProfile(luaBuilder, SkillSet.FromTextContent(
                        BuiltInFullLuaSkillText.SkillName,
                        BuiltInFullLuaSkillText.SkillDescription,
                        fullLuaSkillOverride ?? BuiltInFullLuaSkillText.Instructions));

                    // WHY here: the CoreAI scope registers the default sub-agents before this scope exists, so
                    // the ones over the Lua tools (the scripter) can only join once those tools are in the profile.
                    container.ResolveOrDefault<CoreAiDefaultSubAgents>()?.RegisterAvailable(
                        container.ResolveOrDefault<Ai.SubAgents.ISubAgentRegistry>(), policy);
                }
                catch (VContainerException)
                {
                    // Minimal containers (tests, headless tools) may omit the orchestration services; the
                    // Lua tools are an additive convenience, not a requirement.
                }
            });
        }

        /// <summary>
        /// The character world the agents read (always the published one, through the session), and the agents
        /// themselves: the NPC listener, the player autopilot and <c>/autopilot</c>, the villager brains. A host
        /// registration of <see cref="CoreAI.Mods.Rbx.Agents.IRbxCharacterWorld"/> or <see cref="CoreAiWorldAgents"/>
        /// wins. The first world is attached once the container built; every staged world when it is built.
        /// </summary>
        private static void RegisterWorldAgents(IContainerBuilder builder)
        {
            if (!builder.Exists(typeof(CoreAI.Mods.Rbx.Agents.IRbxCharacterWorld), true))
            {
                builder.Register<CoreAI.Mods.Rbx.Agents.IRbxCharacterWorld>(c =>
                        new CoreAI.Mods.Rbx.Agents.DelegateRbxCharacterWorld(
                            () => c.Resolve<RbxWorldRuntimeSessionController>().CurrentRbxApi?.Game,
                            part =>
                            {
                                IPartPropertySink sink =
                                    c.Resolve<RbxWorldRuntimeSessionController>().CurrentRbxApi?.PartSink;
                                return sink != null && sink.TryGetPartProperties(part.Id, out PartProperties pose)
                                    ? pose.CFrame
                                    : (Mods.Rbx.Datatypes.RbxCFrame?)null;
                            },
                            (part, cframe) => c.Resolve<RbxWorldRuntimeSessionController>().CurrentRbxApi?.PartSink
                                ?.SetCFrame(part.Id, cframe)),
                    Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(CoreAiWorldAgents)))
            {
                builder.Register(c => new CoreAiWorldAgents(
                        c.Resolve<CoreAI.Mods.Rbx.Agents.IRbxCharacterWorld>(),
                        c.ResolveOrDefault<CoreAI.Decisions.IDecisionClientProvider>(),
                        c.ResolveOrDefault<CoreAI.Decisions.IDecisionClock>(),
                        c.ResolveOrDefault<CoreAI.Decisions.IDecisionTraceSink>(),
                        c.ResolveOrDefault<CoreAI.Decisions.IDecisionRouter>(),
                        c.ResolveOrDefault<CoreAI.Mods.Rbx.Agents.RbxCharacterAutopilotOptions>(),
                        c.ResolveOrDefault<CoreAI.ChatOverlay.IChatCommandRegistry>(),
                        c.ResolveOrDefault<CoreAI.ChatOverlay.IChatOverlayMessages>(),
                        c.ResolveOrDefault<CoreAI.Ai.Npc.INpcDirector>(),
                        c.ResolveOrDefault<CoreAI.Ai.Npc.NpcListenerPositionRelay>()),
                    Lifetime.Singleton);
            }

            builder.RegisterBuildCallback(container =>
            {
                try
                {
                    // WHY only with a decision block or an NPC listener: without them there is nothing to run, and
                    // resolving the session here would build the world in containers that never asked for it.
                    if (container.ResolveOrDefault<CoreAI.Decisions.IDecisionClientProvider>() == null
                        && container.ResolveOrDefault<CoreAI.Ai.Npc.NpcListenerPositionRelay>() == null)
                    {
                        return;
                    }

                    CoreAiWorldAgents agents = container.Resolve<CoreAiWorldAgents>();
                    agents.AttachWorld(container.Resolve<RbxWorldRuntimeSessionController>().CurrentRbxApi?.Scheduler);
                }
                catch (VContainerException)
                {
                    // WHY: minimal containers (tests, headless tools) may omit the world session's services.
                }
            });
        }

        /// <summary>
        /// What every world this composition builds gets, the initial one and each one a load stages: the
        /// in-game chat factory, the runtime UI Lua surface (<c>UxmlService</c>) and the player camera preset.
        /// </summary>
        /// <remarks>
        /// WHY one helper for both sites: the initial world and a staged replacement are built in two
        /// factories, and a service attached in only one of them silently disappears after a world load.
        /// </remarks>
        private static void AttachWorldServices(IObjectResolver c, LuaCsRbxApiBindings rbxApi)
        {
            IInGameLlmChatServiceFactory chatFactory = c.ResolveOrDefault<IInGameLlmChatServiceFactory>();
            if (chatFactory != null)
            {
                rbxApi.AttachChatFactory(chatFactory);
            }

            RuntimeUiLuaInstaller.AttachRuntimeUi(c, rbxApi);
            CoreAiPlayerListBindings.AttachTo(
                rbxApi,
                c.ResolveOrDefault<RuntimeUiLuaSurface>()?.Backend,
                c.ResolveOrDefault<CoreAI.Mods.Rbx.Instances.RuntimeUi.RbxPlayerListOptions>());
            // WHY: without a chat (a headless world) TextChatCommands still register; nothing runs them.
            CoreAI.ChatOverlay.IChatCommandRegistry chatCommands =
                c.ResolveOrDefault<CoreAI.ChatOverlay.IChatCommandRegistry>();
            if (chatCommands != null)
            {
                rbxApi.AttachChatCommands(new ChatCommandRegistrySink(chatCommands));
            }

            ChatOverlayWorldChatHost chatHost = c.ResolveOrDefault<ChatOverlayWorldChatHost>();
            if (chatHost != null)
            {
                rbxApi.AttachChatHost(chatHost);
            }

            CoreAiPlayerCameraSettings camera = c.ResolveOrDefault<CoreAiPlayerCameraSettings>();
            if (camera != null)
            {
                rbxApi.CameraModule = camera.CreateCameraModule();
            }
        }

        /// <summary>
        /// The gameplay bindings every world's mod stack gets, the initial one and each one a load stages: the
        /// host's additions plus the Lua AI surface (<c>AIService</c>, <c>TextGenerator</c>), which stays refused
        /// until the framework setting <see cref="CoreAiLuaAiOptions.AllowAgentsFromLua"/> allows it.
        /// </summary>
        private static System.Action<LuaCsApiRegistry, LuaCapabilities> ComposeGameplayBindings(
            IObjectResolver c,
            LuaCsRbxApiBindings rbxApi,
            System.Action<LuaCsApiRegistry, LuaCapabilities> additionalGameplayBindings)
        {
            System.Action<LuaCsApiRegistry, LuaCapabilities> withData = CoreAiLuaDataStoreBindings.Attach(
                rbxApi,
                c.ResolveOrDefault<CoreAI.Mods.Rbx.Instances.DataStores.RbxDataStoreBackend>(),
                additionalGameplayBindings);
            return CoreAiLuaAiBindings.Attach(
                rbxApi,
                c.ResolveOrDefault<CoreAiLuaAiOptions>(),
                c.ResolveOrDefault<IAiOrchestrationService>(),
                c.ResolveOrDefault<CoreAI.Ai.Npc.INpcDirector>(),
                withData,
                RbxBoundObjectAnchors.CreateResolver(rbxApi.PartSink),
                c.ResolveOrDefault<CoreAI.Ai.Npc.NpcSpeechAnchorRegistry>(),
                c.ResolveOrDefault<CoreAI.Ai.Npc.ITextModerationFilter>(),
                null,
                null,
                c.ResolveOrDefault<CoreAI.ChatOverlay.ChatAgentTurns>());
        }

        /// <summary>
        /// The bridge between every world's <c>TextChatService</c> messages and the in-world chat services of the
        /// CoreAI scope (the command registry, the overlay's hooks and feed, the surface switch). Without them (a
        /// scope with no chat) it runs over an empty registry and nothing else; a host registration wins.
        /// </summary>
        private static void RegisterWorldChatHost(IContainerBuilder builder)
        {
            if (builder.Exists(typeof(ChatOverlayWorldChatHost)))
            {
                return;
            }

            builder.Register(c =>
            {
                CoreAI.ChatOverlay.IChatCommandRegistry registry =
                    c.ResolveOrDefault<CoreAI.ChatOverlay.IChatCommandRegistry>()
                    ?? new CoreAI.ChatOverlay.ChatCommandRegistry();
                CoreAI.ChatOverlay.ChatOverlayController overlay =
                    c.ResolveOrDefault<CoreAI.ChatOverlay.ChatOverlayController>();
                return new ChatOverlayWorldChatHost(registry, overlay, overlay?.Feed,
                    c.ResolveOrDefault<CoreAI.ChatOverlay.IChatOverlaySurfaceSwitch>(), overlay, overlay?.Options,
                    c.ResolveOrDefault<CoreAI.Logging.ILog>());
            }, Lifetime.Singleton);
        }

        /// <summary>
        /// Builds the Unity motor for a Humanoid, or null when its character has no backing body.
        /// </summary>
        /// <remarks>
        /// WHY null rather than a throw: a Humanoid can legitimately exist before its
        /// HumanoidRootPart has materialized, and a world with no scene binding (headless tests,
        /// storage-only trees) has no bodies at all. The Humanoid then keeps its health and state
        /// and simply does not move, which is the documented null-motor behaviour.
        /// </remarks>
        private static IRbxCharacterMotor CreateCharacterMotor(
            InstanceGameObjectBinder binder,
            RbxHumanoid humanoid,
            Mods.Rbx.Binding.RbxWorldHost physicsHost,
            Mods.Rbx.Binding.IRbxCharacterMotorProvider hostProvider)
        {
            RbxInstance root = humanoid?.RootPart;
            if (binder == null || root == null || root.IsDestroyed)
            {
                return null;
            }

            if (!binder.TryGetBoundObject(root.Id, out GameObject body) || body == null)
            {
                return null;
            }

            Rigidbody rigidbody = body.GetComponent<Rigidbody>();
            if (rigidbody == null)
            {
                return null;
            }

            // WHY the host is asked for its port on every read instead of capturing the port: a
            // world loaded at runtime disposes the port and builds a new one, and a jump solved
            // against a dead port's gravity would silently use the previous world's value.
            // WHY gravity at all: these bodies have Rigidbody.useGravity off — the world's own
            // acceleration is applied by the port — so a height-based jump solved against
            // Physics.gravity reaches the wrong height and ignores Workspace.Gravity entirely.
            System.Func<float> worldGravity = () =>
                physicsHost?.PhysicsPort?.GravityMetresPerSecondSquared.magnitude
                ?? Mods.Rbx.Spatial.RbxSpace.AccelerationToUnity(
                    (float)RbxWorldPhysics.DefaultGravity);

            // WHY the host is asked first and may decline: a game that already owns a character
            // controller should drive its own bodies, and CoreAI's motor is the answer for
            // everything it does not claim. Declining is a null return, decided with the body in
            // hand — see IRbxCharacterMotorProvider.
            IRbxCharacterMotor hostMotor = TryCreateHostMotor(
                hostProvider, humanoid, body, worldGravity, physicsHost?.Registry);
            if (hostMotor != null)
            {
                return hostMotor;
            }

            return new UnityRbxCharacterMotor(
                rigidbody,
                worldGravityMetresPerSecondSquared: worldGravity);
        }

        /// <summary>
        /// Asks the host provider for a motor, tolerating both a destroyed provider and one whose
        /// decision throws.
        /// </summary>
        /// <remarks>
        /// WHY this is not a plain <c>hostProvider?.TryCreate(...)</c>: <paramref name="hostProvider"/>
        /// is held through the <c>IRbxCharacterMotorProvider</c> interface, not through
        /// <see cref="UnityEngine.Object"/>, so C#'s null-conditional operator resolves to plain
        /// reference equality — it never runs Unity's overloaded <c>==</c>, which is the only thing
        /// that recognises a destroyed native object. A <c>RbxCharacterMotorProviderBehaviour</c>
        /// whose scene unloaded while this lifetime scope survives it is invisible to <c>?.</c> for
        /// exactly that reason, so the call reached into a destroyed MonoBehaviour and threw from
        /// inside its own dead scene/controller references — with nothing here to catch it, character
        /// creation failed outright instead of falling back.
        /// <para>
        /// WHY a destroyed or throwing provider falls back to CoreAI's own motor rather than
        /// propagating: a Humanoid built with no motor gets <c>NullRbxCharacterMotor</c> — a
        /// permanent no-op — which is strictly worse than CoreAI's default motor for every character
        /// it would apply to. The host's provider is opt-in extra behaviour; losing it for one
        /// character should cost that character CoreAI's own motor, not the ability to move at all.
        /// </para>
        /// <para>
        /// WHY the failure still has to be visible: silently swallowing it would leave a host
        /// debugging "why is my controller not driving this character" with no signal at all. It is
        /// reported through the same <see cref="InstanceRegistry.Diagnostics"/> sink the character
        /// pipeline already uses for its own composition faults — see <c>RbxPlayers.LogFailedAutoLoad</c>
        /// for the identical pattern.
        /// </para>
        /// </remarks>
        private static IRbxCharacterMotor TryCreateHostMotor(
            Mods.Rbx.Binding.IRbxCharacterMotorProvider hostProvider,
            RbxHumanoid humanoid,
            GameObject body,
            System.Func<float> worldGravity,
            InstanceRegistry diagnosticsRegistry)
        {
            if (hostProvider == null)
            {
                return null;
            }

            if (hostProvider is UnityEngine.Object unityProvider && unityProvider == null)
            {
                diagnosticsRegistry?.Diagnostics?.Invoke(
                    "[CoreAiMods] The registered IRbxCharacterMotorProvider for '" + humanoid.Name +
                    "' is a destroyed Unity object (its scene likely unloaded while this lifetime " +
                    "scope survived it) — falling back to CoreAI's own motor so the character can " +
                    "still move.");
                return null;
            }

            try
            {
                return hostProvider.TryCreate(humanoid, body, worldGravity);
            }
            catch (System.Exception exception)
            {
                diagnosticsRegistry?.Diagnostics?.Invoke(
                    "[CoreAiMods] IRbxCharacterMotorProvider.TryCreate threw for '" + humanoid.Name +
                    "' — falling back to CoreAI's own motor so the character can still move: " +
                    exception);
                return null;
            }
        }

        private static LuaCsModStack CreateSessionStack(
            IObjectResolver container,
            LuaCsRbxApiBindings rbxApi,
            ILuaModSourceStore sourceStore,
            ILuaModStore modStore,
            ILuaScriptVersionStore versionStore,
            System.Collections.Generic.IEnumerable<string> allowedLuaScenes,
            IFullLuaAccessBlacklistPolicy fullLuaBlacklistPolicy,
            bool enableFullLuaPrivateAccess,
            LuaCapabilities scriptCapabilities,
            LuaCapabilities oneOffCapabilities,
            System.Action<LuaCsApiRegistry, LuaCapabilities> additionalGameplayBindings)
        {
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = container.Resolve<IGameLogger>(),
                LuaScriptVersions = versionStore,
                DataOverlayVersions = container.ResolveOrDefault<IDataOverlayVersionStore>(),
                CommandSink = container.Resolve<IAiGameCommandSink>(),
                PrefabRegistry = container.ResolveOrDefault<ICoreAiPrefabRegistry>(),
                AllowedScenes = allowedLuaScenes,
                FullBlacklistPolicy = fullLuaBlacklistPolicy,
                AllowNonPublicFullMembers = enableFullLuaPrivateAccess,
                ModStore = modStore,
                ModSourceStore = sourceStore,
                Log = container.ResolveOrDefault<Logging.ILog>(),
                LogService = container.ResolveOrDefault<ILuaLogService>(),
                ExecutionObserver = container.ResolveOrDefault<ILuaExecutionObserver>(),
                Observability = container.ResolveOrDefault<IRbxRuntimeObservabilitySink>()
                    ?? NullRbxRuntimeObservabilitySink.Instance,
                WorldMutationGate = container.Resolve<IConfirmedWorldMutationGate>(),
                LocalActorResolver = () =>
                    (container.ResolveOrDefault<IActorIdentityProvider>()
                     ?? CoreServicesInstaller.DefaultLocalHostIdentityProvider)
                    .GetActorContext(BuiltInAgentRoleIds.Programmer),
                Capabilities = scriptCapabilities,
                OneOffCapabilities = oneOffCapabilities,
                AdditionalGameplayBindings = ComposeGameplayBindings(container, rbxApi, additionalGameplayBindings),
                FrameYielder = PlayerLoopScriptFrameYielder.Instance,
                RbxApi = rbxApi,
                // WHY rbxApi.CoroutineResumeBudget and not a fresh resolve: this session's rbxApi already
                // settled on one live budget instance (see the LuaCsRbxApiBindings constructions above);
                // reusing it here keeps this stack's engine reading the exact same object
                // ScriptContext:SetTimeout mutates.
                CoroutineResumeBudget = rbxApi.CoroutineResumeBudget,
                RegisterWorldEditBuildBindings = false
            });
            return stack;
        }

        /// <summary>
        /// The session controller's per-stack teardown hook. Empty: every session stack comes from
        /// <see cref="LuaCsModRuntimeFactory.Create"/> with its Rbx API, which already wires the teardown.
        /// </summary>
        private static void WireSessionTeardown(
            LuaCsModStack stack,
            LuaCsRbxApiBindings rbxApi)
        {
        }

        private static void BindPersistedActorAttribution(
            ILuaModSourceStore sourceStore,
            InstanceRegistry registry,
            ActorContext compositionActor)
        {
            if (sourceStore == null || registry == null)
            {
                return;
            }

            System.Collections.Generic.IReadOnlyList<LuaModManifest> manifests;
            try
            {
                manifests = sourceStore.List()
                    ?? System.Array.Empty<LuaModManifest>();
            }
            catch (System.Exception)
            {
                return;
            }

            foreach (LuaModManifest manifest in manifests)
            {
                string modId = manifest?.Id?.Trim() ?? "";
                string ownerActorId = manifest?.OwnerActorId?.Trim() ?? "";
                if (modId.Length == 0 || ownerActorId.Length == 0)
                {
                    continue;
                }

                string originTag = OriginTag.FromMod(modId);
                if (compositionActor.IsTrusted
                    && compositionActor.Grants.IsUnrestricted
                    && string.Equals(ownerActorId, compositionActor.ActorId,
                        System.StringComparison.Ordinal))
                {
                    registry.ClearActorAttribution(modId, originTag);
                    continue;
                }

                registry.BindActorAttribution(modId, originTag, ownerActorId);
            }
        }

        private sealed class InitialLuaWorldSession
        {
            public InitialLuaWorldSession(
                LuaCsModStack stack,
                LuaCsRbxApiBindings rbxApi,
                ILuaModSourceStore sourceStore)
            {
                Stack = stack;
                RbxApi = rbxApi;
                SourceStore = sourceStore;
            }

            public LuaCsModStack Stack { get; }

            public LuaCsRbxApiBindings RbxApi { get; }

            public ILuaModSourceStore SourceStore { get; }
        }

        private sealed class WorldSessionSourceBackend
        {
            public WorldSessionSourceBackend(ILuaModSourceStore store)
            {
                Store = store
                    ?? throw new System.ArgumentNullException(nameof(store));
            }

            public ILuaModSourceStore Store { get; }
        }

        private sealed class ActorAttributedLuaModRuntime : ILuaModRuntime
        {
            private readonly ILuaModRuntime _inner;
            private readonly InstanceRegistry _registry;

            public ActorAttributedLuaModRuntime(ILuaModRuntime inner, InstanceRegistry registry)
            {
                _inner = inner ?? throw new System.ArgumentNullException(nameof(inner));
                _registry = registry ?? throw new System.ArgumentNullException(nameof(registry));
            }

            public System.Collections.Generic.IReadOnlyList<LuaModInfo> ListMods(ActorContext caller)
            {
                return _inner.ListMods(caller);
            }

            public bool TryGetModSource(ActorContext caller, string id, out string source)
            {
                return _inner.TryGetModSource(caller, id, out source);
            }

            public void LoadMod(ActorContext caller, string id, string luaCode,
                LuaCapabilities capabilities = LuaCapabilities.All,
                bool persistToStore = true)
            {
                string modId = NormalizeModId(id);
                ActorAttributionSnapshot snapshot = Capture(modId);
                PrepareNewOwner(caller, modId);
                try
                {
                    _inner.LoadMod(caller, id, luaCode, capabilities, persistToStore);
                }
                catch
                {
                    Restore(modId, snapshot);
                    throw;
                }
            }

            public string GetModOwnerActorId(ActorContext caller, string id)
            {
                return _inner.GetModOwnerActorId(caller, id);
            }

            public void ReloadMod(ActorContext caller, string id, string luaCode)
            {
                string modId = NormalizeModId(id);
                PrepareExistingOwner(caller, modId);
                _inner.ReloadMod(caller, id, luaCode);
            }

            public ModReloadReport ReloadMod(ActorContext caller, string id, string luaCode,
                ModReloadMode mode)
            {
                string modId = NormalizeModId(id);
                PrepareExistingOwner(caller, modId);
                return _inner.ReloadMod(caller, id, luaCode, mode);
            }

            public bool UnloadMod(ActorContext caller, string id)
            {
                return _inner.UnloadMod(caller, id);
            }

            public string ExportMod(ActorContext caller, string id)
            {
                return _inner.ExportMod(caller, id);
            }

            public bool ImportMod(ActorContext caller, string bundleJson,
                LuaCapabilities hostGrant, bool allowFull = false)
            {
                string modId = ReadBundleModId(bundleJson);
                ActorAttributionSnapshot snapshot = Capture(modId);
                PrepareNewOwner(caller, modId);
                try
                {
                    bool imported = _inner.ImportMod(caller, bundleJson, hostGrant, allowFull);
                    if (!imported)
                    {
                        Restore(modId, snapshot);
                    }

                    return imported;
                }
                catch
                {
                    Restore(modId, snapshot);
                    throw;
                }
            }

            public bool ForgetMod(ActorContext caller, string id)
            {
                bool forgotten = _inner.ForgetMod(caller, id);
                if (forgotten)
                {
                    string modId = NormalizeModId(id);
                    if (modId.Length > 0)
                    {
                        _registry.ClearActorAttribution(modId, OriginTag.FromMod(modId));
                    }
                }

                return forgotten;
            }

            public System.Collections.Generic.IReadOnlyList<LuaScriptRevision> ListModVersions(
                ActorContext caller, string id)
            {
                return _inner.ListModVersions(caller, id);
            }

            public bool TryRevertMod(ActorContext caller, string id, int revisionIndex,
                out string restoredSource)
            {
                string modId = NormalizeModId(id);
                PrepareExistingOwner(caller, modId);
                return _inner.TryRevertMod(caller, id, revisionIndex, out restoredSource);
            }

            public System.Collections.Generic.IReadOnlyList<LuaModHandlerError> GetRecentHandlerErrors(
                ActorContext caller, string modId = null)
            {
                return _inner.GetRecentHandlerErrors(caller, modId);
            }

            public System.Collections.Generic.IReadOnlyList<LuaModReport> GetRecentReports(
                ActorContext caller, string modId = null)
            {
                return _inner.GetRecentReports(caller, modId);
            }

            public int ClearRecentHandlerErrors(ActorContext caller, string modId = null)
            {
                return _inner.ClearRecentHandlerErrors(caller, modId);
            }

            public int ClearRecentReports(ActorContext caller, string modId = null)
            {
                return _inner.ClearRecentReports(caller, modId);
            }

            public void Tick(ActorContext caller, double deltaSeconds)
            {
                _inner.Tick(caller, deltaSeconds);
            }

            public void EmitEvent(ActorContext caller, string name, string payload = "")
            {
                _inner.EmitEvent(caller, name, payload);
            }

            public bool IsLoaded(ActorContext caller, string id)
            {
                return _inner.IsLoaded(caller, id);
            }

            public bool GetModReportLoggingEnabled(ActorContext caller, string id)
            {
                return _inner.GetModReportLoggingEnabled(caller, id);
            }

            public bool SetModReportLoggingEnabled(ActorContext caller, string id, bool enabled)
            {
                return _inner.SetModReportLoggingEnabled(caller, id, enabled);
            }

            public void AddModHandlerErroredListener(ActorContext caller,
                System.Action<string, string, int> listener)
            {
                _inner.AddModHandlerErroredListener(caller, listener);
            }

            public void RemoveModHandlerErroredListener(ActorContext caller,
                System.Action<string, string, int> listener)
            {
                _inner.RemoveModHandlerErroredListener(caller, listener);
            }

            public void AddModSourceLoadedListener(ActorContext caller,
                System.Action<string, string, LuaCapabilities> listener)
            {
                _inner.AddModSourceLoadedListener(caller, listener);
            }

            public void RemoveModSourceLoadedListener(ActorContext caller,
                System.Action<string, string, LuaCapabilities> listener)
            {
                _inner.RemoveModSourceLoadedListener(caller, listener);
            }

            public void AddModSourceUnloadedListener(ActorContext caller,
                System.Action<string, string, LuaCapabilities> listener)
            {
                _inner.AddModSourceUnloadedListener(caller, listener);
            }

            public void RemoveModSourceUnloadedListener(ActorContext caller,
                System.Action<string, string, LuaCapabilities> listener)
            {
                _inner.RemoveModSourceUnloadedListener(caller, listener);
            }

            public void AddModEventEmittedListener(ActorContext caller,
                System.Action<string, string, string> listener)
            {
                _inner.AddModEventEmittedListener(caller, listener);
            }

            public void RemoveModEventEmittedListener(ActorContext caller,
                System.Action<string, string, string> listener)
            {
                _inner.RemoveModEventEmittedListener(caller, listener);
            }

            public void AddModReportEmittedListener(ActorContext caller,
                System.Action<string, string> listener)
            {
                _inner.AddModReportEmittedListener(caller, listener);
            }

            public void RemoveModReportEmittedListener(ActorContext caller,
                System.Action<string, string> listener)
            {
                _inner.RemoveModReportEmittedListener(caller, listener);
            }

            private void PrepareNewOwner(ActorContext caller, string modId)
            {
                DemandTrusted(caller);
                if (modId.Length == 0)
                {
                    return;
                }

                string originTag = OriginTag.FromMod(modId);
                if (caller.Grants.IsUnrestricted)
                {
                    _registry.ClearActorAttribution(modId, originTag);
                    return;
                }

                _registry.BindActorAttribution(modId, originTag, caller.ActorId);
            }

            private void PrepareExistingOwner(ActorContext caller, string modId)
            {
                DemandTrusted(caller);
                if (modId.Length == 0)
                {
                    return;
                }

                string ownerActorId = _inner.GetModOwnerActorId(caller, modId)?.Trim() ?? "";
                string originTag = OriginTag.FromMod(modId);
                if (ownerActorId.Length == 0
                    || (caller.Grants.IsUnrestricted
                        && string.Equals(ownerActorId, caller.ActorId,
                            System.StringComparison.Ordinal)))
                {
                    _registry.ClearActorAttribution(modId, originTag);
                    return;
                }

                _registry.BindActorAttribution(modId, originTag, ownerActorId);
            }

            private ActorAttributionSnapshot Capture(string modId)
            {
                if (modId.Length == 0)
                {
                    return new ActorAttributionSnapshot(false, null);
                }

                bool found = _registry.TryGetActorAttribution(
                    modId, OriginTag.FromMod(modId), out string actorId);
                return new ActorAttributionSnapshot(found, actorId);
            }

            private void Restore(string modId, ActorAttributionSnapshot snapshot)
            {
                if (modId.Length == 0)
                {
                    return;
                }

                string originTag = OriginTag.FromMod(modId);
                if (snapshot.Found)
                {
                    _registry.BindActorAttribution(modId, originTag, snapshot.ActorId);
                    return;
                }

                _registry.ClearActorAttribution(modId, originTag);
            }

            private static string NormalizeModId(string id)
            {
                return id?.Trim() ?? "";
            }

            private static string ReadBundleModId(string bundleJson)
            {
                try
                {
                    ActorAttributionBundle bundle =
                        JsonConvert.DeserializeObject<ActorAttributionBundle>(bundleJson);
                    return bundle?.Manifest?.Id?.Trim() ?? "";
                }
                catch (JsonException)
                {
                    return "";
                }
            }

            private static void DemandTrusted(ActorContext caller)
            {
                if (!caller.IsTrusted)
                {
                    throw new System.InvalidOperationException(
                        "Actor context was not issued by an identity provider.");
                }
            }

            private readonly struct ActorAttributionSnapshot
            {
                public ActorAttributionSnapshot(bool found, string actorId)
                {
                    Found = found;
                    ActorId = actorId;
                }

                public bool Found { get; }

                public string ActorId { get; }
            }

            private sealed class ActorAttributionBundle
            {
                public LuaModManifest Manifest = new();
            }
        }
    }

    /// <summary>
    /// Play-mode startup order of the Rbx world, kept out of the build callback so it runs without
    /// play mode in tests: the durable startup selection is restored first, and the default world's
    /// persisted mods rehydrate only when nothing was restored.
    /// </summary>
    internal static class RbxWorldStartupSequence
    {
        /// <summary>
        /// Restores the startup selection, then runs <paramref name="rehydrateDefault"/> unless the
        /// selection is now the live world. Never throws: a failure keeps the default world and is
        /// reported to <paramref name="diagnostics"/>. No thread and no blocking wait (WebGL).
        /// </summary>
        public static async Cysharp.Threading.Tasks.UniTask<RbxWorldStartupRestoreOutcome> RunAsync(
            IRbxWorldStartupSelection startupSelection,
            System.Action rehydrateDefault,
            System.Action<string> diagnostics,
            System.Threading.CancellationToken cancellationToken = default)
        {
            RbxWorldStartupRestoreOutcome outcome = RbxWorldStartupRestoreOutcome.NotSelected;
            if (startupSelection != null)
            {
                try
                {
                    RbxWorldStartupRestoreResult restored =
                        await startupSelection.RestoreStartupSelectionAsync(cancellationToken);
                    outcome = restored?.Outcome ?? RbxWorldStartupRestoreOutcome.NotSelected;
                }
                catch (System.Exception ex)
                {
                    outcome = RbxWorldStartupRestoreOutcome.FellBack;
                    Report(diagnostics, "The startup world restore failed, so the default world opens: "
                                        + ex.Message);
                }
            }

            if (outcome == RbxWorldStartupRestoreOutcome.Restored)
            {
                return outcome;
            }

            try
            {
                rehydrateDefault?.Invoke();
            }
            catch (System.Exception ex)
            {
                Report(diagnostics, "Rehydrating the default world's persisted mods failed: " + ex.Message);
            }

            return outcome;
        }

        private static void Report(System.Action<string> diagnostics, string message)
        {
            try
            {
                diagnostics?.Invoke(message);
            }
            catch (System.Exception)
            {
            }
        }
    }
}
