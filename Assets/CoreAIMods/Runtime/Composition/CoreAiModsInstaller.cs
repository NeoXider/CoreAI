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
        public static void RegisterCoreAiMods(
            this IContainerBuilder builder,
            System.Collections.Generic.IEnumerable<string> allowedLuaScenes = null,
            bool enableFullLuaAccess = false,
            bool enableFullLuaPrivateAccess = false,
            IFullLuaAccessBlacklistPolicy fullLuaBlacklistPolicy = null)
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
            builder.Register(_ => new FileLuaModStore(), Lifetime.Singleton).As<ILuaModStore>();
            builder.Register(_ => new FileLuaModSourceStore(), Lifetime.Singleton).As<ILuaModSourceStore>();

            // Mods shipped with the build: Resources/CoreAIMods/*.lua are seeded into the store on startup
            // (before rehydrate) so a game can ship with a ready-made set of mods. Hosts register additional
            // IBundledModSource entries (StreamingAssets, Addressables, remote) to extend the set.
            builder.Register<IBundledModSource>(_ => new ResourcesBundledModSource(), Lifetime.Singleton);

            // Build the whole Lua-CSharp stack once from container services (sandbox + gameplay bindings +
            // persistent runtime + one-off executor), sharing one bindings instance across both surfaces.
            builder.Register(c => LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
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
                OneOffCapabilities = oneOffCapabilities
            }), Lifetime.Singleton);

            // Facades over the stack: the persistent runtime as the VM-agnostic ILuaModRuntime (manage_mods +
            // auto-repair) and its concrete type (rehydrate + tick), and the one-off executor as ILuaExecutor.
            builder.Register(c => c.Resolve<LuaCsModStack>().Runtime, Lifetime.Singleton)
                .AsSelf().As<ILuaModRuntime>();
            builder.Register(c => c.Resolve<LuaCsModStack>().ToolExecutor, Lifetime.Singleton)
                .As<LuaTool.ILuaExecutor>();
            // The shared logic-override slots both surfaces register through, so a host (e.g. a demo that
            // declares gameplay formula slots) resolves the same instance every mod's logic_define writes to.
            builder.Register(c => c.Resolve<LuaCsModStack>().GameplayBindings.LogicSlots, Lifetime.Singleton)
                .AsSelf();

            // Startup rehydration + frame ticker (play mode only). EditMode containers share the REAL
            // persistent store, so rehydrating there would inject earlier-run mods into every fresh
            // container; and DontDestroyOnLoad throws outside play mode. EditMode consumers load and Tick
            // explicitly. Best-effort: a rehydrate failure never aborts container construction.
            builder.RegisterBuildCallback(container =>
            {
                try
                {
                    Logging.Log.Instance.Info(
                        $"[CoreAI] CoreAiMods (Lua-CSharp): capability grant = {scriptCapabilities} " +
                        $"(enableFullLuaAccess={enableFullLuaAccess})");

                    if (Application.isPlaying)
                    {
                        LuaCsModRuntime runtime = container.Resolve<LuaCsModStack>().Runtime;

                        GameObject tickerGo = new("CoreAI_LuaModTicker");
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

                            tickerGo.AddComponent<LuaModRuntimeTickDriver>().Initialize(runtime);
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

            // Native tool-calling path for the built-in Programmer role: execute_lua + manage_mods over the
            // same Lua-CSharp sandbox and gameplay bindings. Hosts that attach their own tools per role
            // override via AgentMemoryPolicy.
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

                    // Full Lua reference on demand (read_skill) so the system prompt stays small.
                    // A Resources/AgentSkills/LuaModding TextAsset overrides the built-in text, same
                    // convention as the AgentPrompts/System overrides.
                    TextAsset skillOverride = Resources.Load<TextAsset>("AgentSkills/LuaModding");
                    policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                        BuiltInLuaModdingSkillText.SkillName,
                        BuiltInLuaModdingSkillText.SkillDescription,
                        skillOverride != null ? skillOverride.text : BuiltInLuaModdingSkillText.Instructions));
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
