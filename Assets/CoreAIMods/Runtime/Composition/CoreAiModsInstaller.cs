using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;
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
    /// bindings + persistent runtime + one-off executor) and attaches the <c>execute_lua</c> +
    /// <c>manage_mods</c> + <c>get_mod_logs</c> tools (and the Lua Modding skill) to the built-in
    /// Programmer role.
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
        public static void RegisterCoreAiMods(
            this IContainerBuilder builder,
            System.Collections.Generic.IEnumerable<string> allowedLuaScenes = null,
            bool enableFullLuaAccess = false,
            bool enableFullLuaPrivateAccess = false,
            IFullLuaAccessBlacklistPolicy fullLuaBlacklistPolicy = null,
            string modStoreId = null,
            int? worldAclVersion = InstanceRegistry.CurrentWorldAclVersion,
            System.Func<bool> applicationIsPlayingProvider = null,
            System.Func<string, string> skillTextProvider = null)
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
            builder.Register(_ => new FileLuaModSourceStore(storeId: modStoreId), Lifetime.Singleton)
                .As<ILuaModSourceStore>();

            // Mods shipped with the build: Resources/CoreAIMods/*.lua are seeded into the store on startup
            // (before rehydrate) so a game can ship with a ready-made set of mods. Hosts register additional
            // IBundledModSource entries (StreamingAssets, Addressables, remote) to extend the set.
            builder.Register<IBundledModSource>(_ => new ResourcesBundledModSource(), Lifetime.Singleton);

            // WHY: one mod log ring buffer per container backs get_mod_logs (LLM tool + MCP tool); it is
            // consumed via ResolveOrDefault at the stack factory below, so a host that registers its own
            // ILuaLogService wins — the same override story as the mod stores. No mirrorLogger: the mod
            // runtime already writes errors to the scoped log unconditionally, and in production the
            // mirror would resolve to the same game-log sink and duplicate every error entry.
            builder.Register(c => new LuaLogService(), Lifetime.Singleton).As<ILuaLogService>();

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
                            "[CoreAI.RbxApi] " + msg, Logging.LogTag.World));
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
                        log: msg => rbxLog.Warn("[CoreAI.RbxApi] " + msg, Logging.LogTag.World));
                }

                rbxApiHolder[0] = rbxApi;

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
                    ModSourceStore = c.ResolveOrDefault<ILuaModSourceStore>(),
                    Log = c.ResolveOrDefault<Logging.ILog>(),
                    LogService = c.ResolveOrDefault<ILuaLogService>(),
                    ExecutionObserver = c.ResolveOrDefault<ILuaExecutionObserver>(),
                    Observability = observability,
                    Capabilities = scriptCapabilities,
                    OneOffCapabilities = oneOffCapabilities,
                    // WHY: one shared Roblox world too — persistent mods and one-off execute_lua resolve
                    // the same InstanceRegistry/game/workspace, so an instance a mod creates is the one the
                    // console navigates (roadmap §5.1.3). Opt-in per stack; wired here for production.
                    RbxApi = rbxApi,
                    // WHY: the Programmer builds worlds via the Rbx surface and Lua mechanics, not the
                    // low-level coreai_world_* primitives; the WorldEdit capability itself stays granted
                    // because Instance.new requires it.
                    RegisterWorldEditBuildBindings = false
                });

                // WHY (audit H1): an unloaded mod must not leak the Rbx instances or scheduled threads it
                // created, nor keep its signal connections firing after teardown. This single
                // ModTearingDown handler stops threads and connections before the instance sweep, so no
                // surviving execution can observe a just-destroyed instance during the sweep.
                //
                // Connections: mod-owned Connect/Once handles are Disconnected on EVERY reason — Unload,
                // Reload, AND Quarantine — because unlike instances the re-run chunk re-Connects fresh
                // handlers on reload, so the stale ones must always be dropped. Disconnect is idempotent.
                //
                // Instances: swept only on UNLOAD — NOT on Reload (same owner id — the replacement keeps
                // them) nor Quarantine (objects must survive the auto-repair reload). GetOwnedBy returns a
                // snapshot, so destroying while it prunes the registry is safe; RbxInstance.Destroy() is
                // idempotent.
                {
                    Mods.Rbx.Instances.ModConnectionRegistry ownedConnections = rbxApi?.Connections;
                    Mods.Rbx.Instances.InstanceRegistry ownedRegistry = rbxHost?.Registry;
                    Logging.ILog teardownLog = c.ResolveOrDefault<Logging.ILog>();
                    luaCsStack.Runtime.ModTearingDown += (modId, reason) =>
                    {
                        // WHY: on RELOAD the replacement chunk has ALREADY re-Connected (BuildMod runs
                        // before this teardown), so only the outgoing chunk's connections are dropped here,
                        // not the new generation's; Unload/Quarantine have no new chunk, so everything goes.
                        if (reason == LuaModTeardownReason.Reload)
                        {
                            rbxApi?.KillOutgoingScheduledGenerations(modId);
                        }
                        else
                        {
                            rbxApi?.KillAllScheduledOwnedBy(modId);
                        }

                        ownedConnections?.DisconnectOwnedBy(
                            modId, reason == LuaModTeardownReason.Reload);

                        if (ownedRegistry == null || reason != LuaModTeardownReason.Unload)
                        {
                            return;
                        }

                        foreach (Mods.Rbx.Instances.RbxInstance owned in
                                 ownedRegistry.GetTeardownOwnedBy(modId))
                        {
                            try
                            {
                                owned?.Destroy();
                            }
                            catch (System.Exception ex)
                            {
                                teardownLog?.Warn(
                                    $"[CoreAiMods] Destroying an instance owned by unloaded mod '{modId}' failed: {ex.Message}");
                            }
                        }
                    };
                }

                return luaCsStack;
            }, Lifetime.Singleton);

            builder.RegisterDisposeCallback(_ =>
            {
                rbxApiHolder[0]?.Dispose();
                rbxApiHolder[0] = null;
            });

            builder.Register(c => c.Resolve<LuaCsModStack>().Runtime, Lifetime.Singleton)
                .AsSelf();
            builder.Register(c => new ActorAttributedLuaModRuntime(
                    c.Resolve<LuaCsModStack>().Runtime,
                    c.Resolve<LuaCsModStack>().GameplayBindings.RbxApi.Registry),
                Lifetime.Singleton)
                .As<ILuaModRuntime>();
            builder.Register(c => c.Resolve<LuaCsModStack>().ToolExecutor, Lifetime.Singleton)
                .As<LuaTool.ILuaExecutor>();
            builder.Register(c => c.Resolve<LuaCsModStack>().GameplayBindings.LogicSlots, Lifetime.Singleton)
                .AsSelf();

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
                    Object.Destroy(tickerHolder[0]);
                    tickerHolder[0] = null;
                }
            });

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
                        LuaCsModRuntime runtime = stack.Runtime;
                        LuaCsRbxApiBindings stackRbxApi = stack.GameplayBindings.RbxApi;

                        GameObject tickerGo = new("CoreAI_LuaModTicker");
                        tickerHolder[0] = tickerGo;
                        Object.DontDestroyOnLoad(tickerGo);

                        void RehydrateAndStartTicking()
                        {
                            // Seed bundled mods into the store BEFORE rehydrate, so shipped mods load
                            // exactly like persisted ones. Best-effort: a seeding failure never blocks
                            // rehydrate.
                            try
                            {
                                System.Collections.Generic.IEnumerable<IBundledModSource> bundled =
                                    container.Resolve<System.Collections.Generic.IEnumerable<IBundledModSource>>();
                                new BundledModSeeder(
                                    container.Resolve<ILuaModSourceStore>(),
                                    new System.Collections.Generic.List<IBundledModSource>(bundled),
                                    container.ResolveOrDefault<Logging.ILog>()).Seed();
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
                            BindPersistedActorAttribution(
                                container.ResolveOrDefault<ILuaModSourceStore>(),
                                stackRbxApi?.Registry,
                                hostActor);
                            runtime.RehydrateFromStore(scriptCapabilities,
                                (scriptCapabilities & LuaCapabilities.Full) != 0);

                            // WHY: phase-specific pumps preserve Stepped -> delayed work -> input ->
                            // Heartbeat -> RenderStepped before the runtime tick each scaled frame.
                            tickerGo.AddComponent<LuaModRuntimeTickDriver>().Initialize(
                                runtime,
                                hostActor,
                                stackRbxApi?.Scheduler,
                                stackRbxApi != null
                                    ? stackRbxApi.PumpPreSimulation
                                    : (System.Action<float>)null,
                                stackRbxApi != null
                                    ? stackRbxApi.PumpHeartbeat
                                    : (System.Action<float>)null,
                                stackRbxApi != null
                                    ? stackRbxApi.PumpPreRender
                                    : (System.Action<float>)null);
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
                            tickerGo.AddComponent<WorldRestoreGate>().Begin(worldState, RehydrateAndStartTicking);
                        }
                        else
                        {
                            RehydrateAndStartTicking();
                        }
                    }
                }
                catch (VContainerException)
                {
                    // Minimal containers (tests, headless tools) may omit the mod runtime; rehydration
                    // is an additive convenience, not a requirement.
                }
            });

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

                    policy.AddToolForRole(BuiltInAgentRoleIds.Programmer,
                        new LuaLlmTool(
                            container.Resolve<LuaTool.ILuaExecutor>(),
                            settings,
                            log,
                            limiter,
                            actorIdentityProvider,
                            BuiltInAgentRoleIds.Programmer));
                    policy.AddToolForRole(BuiltInAgentRoleIds.Programmer,
                        new LuaModsLlmTool(
                            container.Resolve<ILuaModRuntime>(),
                            settings,
                            log,
                            scriptCapabilities,
                            allowModManagement: true,
                            actorIdentityProvider: actorIdentityProvider,
                            roleId: BuiltInAgentRoleIds.Programmer));
                    policy.AddToolForRole(BuiltInAgentRoleIds.Programmer,
                        new GetModLogsLlmTool(container.Resolve<ILuaLogService>()));

                    string skillOverride = skillTextProvider != null
                        ? skillTextProvider("AgentSkills/LuaModding")
                        : Resources.Load<TextAsset>("AgentSkills/LuaModding")?.text;
                    policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                        BuiltInLuaModdingSkillText.SkillName,
                        BuiltInLuaModdingSkillText.SkillDescription,
                        skillOverride ?? BuiltInLuaModdingSkillText.Instructions));

                    string rbxSkillOverride = skillTextProvider != null
                        ? skillTextProvider("AgentSkills/RbxApi")
                        : Resources.Load<TextAsset>("AgentSkills/RbxApi")?.text;
                    policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                        BuiltInRbxApiSkillText.SkillName,
                        BuiltInRbxApiSkillText.SkillDescription,
                        rbxSkillOverride ?? BuiltInRbxApiSkillText.Instructions));

                    string fullLuaSkillOverride = skillTextProvider != null
                        ? skillTextProvider("AgentSkills/FullLua")
                        : Resources.Load<TextAsset>("AgentSkills/FullLua")?.text;
                    policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                        BuiltInFullLuaSkillText.SkillName,
                        BuiltInFullLuaSkillText.SkillDescription,
                        fullLuaSkillOverride ?? BuiltInFullLuaSkillText.Instructions));
                }
                catch (VContainerException)
                {
                    // Minimal containers (tests, headless tools) may omit the orchestration services; the
                    // Lua tools are an additive convenience, not a requirement.
                }
            });
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
}
