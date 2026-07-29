using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
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
    /// <c>manage_mods</c> tools (and the Lua Modding skill) to the built-in Programmer role.
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
        public static void RegisterCoreAiMods(
            this IContainerBuilder builder,
            System.Collections.Generic.IEnumerable<string> allowedLuaScenes = null,
            bool enableFullLuaAccess = false,
            bool enableFullLuaPrivateAccess = false,
            IFullLuaAccessBlacklistPolicy fullLuaBlacklistPolicy = null,
            string modStoreId = null)
        {
            LuaCapabilities scriptCapabilities = enableFullLuaAccess
                ? LuaCapabilities.All | LuaCapabilities.Full
                : LuaCapabilities.All;

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

            // WHY: one Lua-CSharp stack shared across both surfaces, so persistent runtime and one-off executor
            // resolve the same sandbox + gameplay bindings instance rather than diverging copies.
            builder.Register(c =>
            {
                // WHY: a scene that references a RbxWorldHost on its CoreAiModsLifetimeScope gets a
                // VISIBLE Rbx world - the host's registry/game/binder back the Lua surface, so
                // Instance.new('Part') materializes a GameObject. Without a host the world stays
                // headless in-memory (same API, no rendering). Initialize() is idempotent and safe
                // before Awake ordering.
                Mods.Rbx.Binding.RbxWorldHost rbxHost = c.ResolveOrDefault<Mods.Rbx.Binding.RbxWorldHost>();
                // WHY: the scoped ILog applies the authored GameLogSettings filter; Log.Instance backs
                // minimal containers (tests, headless tools) that never registered one, so the Rbx
                // wiring diagnostics below survive either way.
                Logging.ILog rbxLog = c.ResolveOrDefault<Logging.ILog>() ?? Logging.Log.Instance;
                LuaCsRbxApiBindings rbxApi;
                if (rbxHost != null)
                {
                    rbxHost.SetLog(rbxLog);
                    rbxHost.Initialize();
                    rbxLog.Info(
                        $"[CoreAiMods] RbxWorldHost resolved — registry has {rbxHost.Registry.Count} instances, " +
                        $"binder bound count={rbxHost.Binder.BoundCount}.",
                        Logging.LogTag.World);
                    rbxApi = new LuaCsRbxApiBindings(
                        rbxHost.Registry, rbxHost.Game, partSink: rbxHost.Binder,
                        cameraRig: rbxHost.CameraRig, inputSource: rbxHost.InputSource,
                        pickSource: rbxHost.PickSource);
                }
                else
                {
                    rbxLog.Error(
                        "[CoreAiMods] RbxWorldHost NOT resolved — mods run headless. " +
                        "Instance.new / workspace mutations produce no GameObjects. " +
                        "Check: (1) RbxWorldHost component exists in the scene, " +
                        "(2) CoreAiModsLifetimeScope.robloxWorldHost is wired to it, " +
                        "(3) link.xml preserves CoreAI.RbxApi.Binding assembly.",
                        Logging.LogTag.World);
                    rbxApi = new LuaCsRbxApiBindings(
                        log: msg => rbxLog.Warn("[CoreAI.RbxApi] " + msg, Logging.LogTag.World));
                }

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
                    ExecutionObserver = c.ResolveOrDefault<ILuaExecutionObserver>(),
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

                // WHY (audit H1): an unloaded mod must not leak the Rbx instances it created, nor keep its
                // signal connections firing after teardown. This single ModTearingDown handler releases both
                // in the safe order: connections FIRST, instance sweep SECOND, so no still-connected handler
                // fires against a just-destroyed instance (INSTANCE_DESTROYED) during the sweep.
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
                        // WHY: disconnect BEFORE the instance sweep so a Heartbeat/InputBegan handler is
                        // already dead when its owning instances are destroyed below. On RELOAD the
                        // replacement chunk has ALREADY re-Connected (BuildMod runs before this teardown),
                        // so keep the current generation and drop only the outgoing chunk's connections;
                        // Unload/Quarantine have no new chunk and disconnect everything.
                        ownedConnections?.DisconnectOwnedBy(
                            modId, reason == LuaModTeardownReason.Reload);

                        if (ownedRegistry == null || reason != LuaModTeardownReason.Unload)
                        {
                            return;
                        }

                        foreach (Mods.Rbx.Instances.RbxInstance owned in ownedRegistry.GetOwnedBy(modId))
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

            builder.Register(c => c.Resolve<LuaCsModStack>().Runtime, Lifetime.Singleton)
                .AsSelf().As<ILuaModRuntime>();
            builder.Register(c => c.Resolve<LuaCsModStack>().ToolExecutor, Lifetime.Singleton)
                .As<LuaTool.ILuaExecutor>();
            builder.Register(c => c.Resolve<LuaCsModStack>().GameplayBindings.LogicSlots, Lifetime.Singleton)
                .AsSelf();

            // Startup rehydration + frame ticker (play mode only). EditMode containers share the REAL
            // persistent store, so rehydrating there would inject earlier-run mods into every fresh
            // container; and DontDestroyOnLoad throws outside play mode. EditMode consumers load and Tick
            // explicitly. Best-effort: a rehydrate failure never aborts container construction.
            // WHY: the ticker is DontDestroyOnLoad (its runtime must keep ticking across additive scene
            // loads while the owning scope lives), so scene unload does NOT destroy it. Without an
            // explicit dispose hook every scope build leaks an immortal ticker whose runtime keeps
            // driving persisted mod handlers into scenes/tests that no longer own them (observed:
            // cross-test world-command spam and an eventual editor OOM during full PlayMode runs).
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

                    if (Application.isPlaying)
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

                            runtime.RehydrateFromStore(scriptCapabilities,
                                (scriptCapabilities & LuaCapabilities.Full) != 0);

                            // WHY: the per-frame pump runs as the driver's pre-tick so UserInputService
                            // events and the RunService game-loop signals (Heartbeat/Stepped/
                            // RenderStepped) fire with the frame delta before mod dispatch each frame.
                            tickerGo.AddComponent<LuaModRuntimeTickDriver>().Initialize(
                                runtime, stackRbxApi != null ? stackRbxApi.PumpFrame : (System.Action<float>)null);
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

                    policy.AddToolForRole(BuiltInAgentRoleIds.Programmer,
                        new LuaLlmTool(container.Resolve<LuaTool.ILuaExecutor>(), settings, log, limiter));
                    policy.AddToolForRole(BuiltInAgentRoleIds.Programmer,
                        new LuaModsLlmTool(container.Resolve<ILuaModRuntime>(), settings, log, scriptCapabilities));

                    TextAsset skillOverride = Resources.Load<TextAsset>("AgentSkills/LuaModding");
                    policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                        BuiltInLuaModdingSkillText.SkillName,
                        BuiltInLuaModdingSkillText.SkillDescription,
                        skillOverride != null ? skillOverride.text : BuiltInLuaModdingSkillText.Instructions));

                    TextAsset rbxSkillOverride = Resources.Load<TextAsset>("AgentSkills/RbxApi");
                    policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                        BuiltInRbxApiSkillText.SkillName,
                        BuiltInRbxApiSkillText.SkillDescription,
                        rbxSkillOverride != null ? rbxSkillOverride.text : BuiltInRbxApiSkillText.Instructions));

                    TextAsset fullLuaSkillOverride = Resources.Load<TextAsset>("AgentSkills/FullLua");
                    policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                        BuiltInFullLuaSkillText.SkillName,
                        BuiltInFullLuaSkillText.SkillDescription,
                        fullLuaSkillOverride != null
                            ? fullLuaSkillOverride.text
                            : BuiltInFullLuaSkillText.Instructions));
                }
                catch (VContainerException)
                {
                    // Minimal containers (tests, headless tools) may omit the orchestration services; the
                    // Lua tools are an additive convenience, not a requirement.
                }
            });
        }
    }
}
