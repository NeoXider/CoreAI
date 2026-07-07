using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using MessagePipe;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Registers the entire Lua mod subsystem into a DI container. This is the inverse of the
    /// dependency: the MoonSharp-free core (<see cref="WorldCommandsInstaller.RegisterWorldCommands"/>)
    /// registers only the native world-command executors, while this module installer — living in the
    /// <c>com.neoxider.coreaimods</c> package — adds the sandbox bindings, the mod runtime, the tick
    /// driver, and attaches the <c>execute_lua</c> + <c>manage_mods</c> tools (and the Lua Modding
    /// skill) to the built-in Programmer role.
    /// <para>
    /// Call it on the same container that already ran <see cref="WorldCommandsInstaller.RegisterWorldCommands"/>
    /// (it resolves the world command sink and prefab whitelist from there). In a scene the idiomatic
    /// wiring is a child <see cref="CoreAiModsLifetimeScope"/> parented to the CoreAI scope; tests call
    /// this extension directly on their builder.
    /// </para>
    /// This method was extracted verbatim from the pre-inversion <c>RegisterWorldCommands</c>; the VM is
    /// still MoonSharp here. Swapping the runtime to Lua-CSharp is an internal change behind the
    /// <see cref="IGameLuaRuntimeBindings"/>/<see cref="LuaTool.ILuaExecutor"/> seam.
    /// </summary>
    public static class CoreAiModsInstaller
    {
        /// <param name="builder">Container the core world commands were already registered on.</param>
        /// <param name="allowedLuaScenes">Scene whitelist for the Lua <c>coreai_world_load_scene</c> binding.</param>
        /// <param name="enableFullLuaAccess">
        /// When true, mods loaded through this composition get the Full tier (reflection over arbitrary
        /// GameObjects/components via <see cref="CoreAiFullUnityLuaRuntimeBindings"/>). Off by default.
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
            builder.Register<DefaultDataOverlayPayloadValidator>(Lifetime.Singleton)
                .As<IDataOverlayPayloadValidator>();

            builder.Register<CoreAiVersioningLuaRuntimeBindings>(Lifetime.Singleton);
            // Factory registrations throughout: ctors with host-supplied or optional/default
            // parameters are not resolvable by VContainer's Register<T>.
            builder.Register(c => new CoreAiWorldLuaRuntimeBindings(
                    c.Resolve<IAiGameCommandSink>(),
                    allowedLuaScenes),
                Lifetime.Singleton);
            builder.Register(c => new CoreAiComponentLuaRuntimeBindings(
                    c.Resolve<IAiGameCommandSink>()),
                Lifetime.Singleton);
            builder.Register<LuaTimeBindings>(Lifetime.Singleton);
            builder.Register<CoreAiWorldQueryLuaBindings>(Lifetime.Singleton);
            // Factory: stripping >= Medium removes the unused parameterless ctor on WebGL.
            builder.Register(c => new CoreAiInputLuaRuntimeBindings(),
                Lifetime.Singleton);
            builder.Register(c => new CoreAiFullUnityLuaRuntimeBindings(
                    c.Resolve<IGameLogger>(),
                    enableFullLuaPrivateAccess,
                    fullLuaBlacklistPolicy),
                Lifetime.Singleton);
            LuaCapabilities scriptCapabilities = enableFullLuaAccess
                ? LuaCapabilities.All | LuaCapabilities.Full
                : LuaCapabilities.All;
            builder.Register(c => new LuaLogicSlots(c.Resolve<Logging.ILog>()),
                Lifetime.Singleton);
            builder.Register(c => new AggregatingGameLuaRuntimeBindings(
                    c.Resolve<IGameLogger>(),
                    c.Resolve<CoreAiVersioningLuaRuntimeBindings>(),
                    c.Resolve<CoreAiWorldLuaRuntimeBindings>(),
                    c.Resolve<CoreAiComponentLuaRuntimeBindings>(),
                    c.Resolve<LuaTimeBindings>(),
                    c.Resolve<CoreAiWorldQueryLuaBindings>(),
                    c.Resolve<LuaLogicSlots>(),
                    c.Resolve<CoreAiFullUnityLuaRuntimeBindings>(),
                    scriptCapabilities,
                    c.Resolve<CoreAiInputLuaRuntimeBindings>()), Lifetime.Singleton)
                .As<IGameLuaRuntimeBindings>();
            builder.Register<LoggingLuaExecutionObserver>(Lifetime.Singleton)
                .As<ILuaExecutionObserver>();
            builder.RegisterComponentOnNewGameObject<LuaCoroutineRunner>(Lifetime.Singleton,
                "CoreAI_LuaCoroutineRunner");

            builder.Register(c => new FileLuaModStore(), Lifetime.Singleton)
                .As<ILuaModStore>();
            // Persists mod source + manifest: active mods survive restarts, export/import works.
            builder.Register(c => new FileLuaModSourceStore(), Lifetime.Singleton)
                .As<ILuaModSourceStore>();
            builder.Register(c => new LuaModRuntime(
                    c.Resolve<IGameLuaRuntimeBindings>(),
                    c.Resolve<ILuaModStore>(),
                    sourceStore: c.Resolve<ILuaModSourceStore>(),
                    autoPersistMods: true,
                    // Reuse the host's ILuaScriptVersionStore so mod load/reload records a revision per edit
                    // (keyed by mod id) and `manage_mods versions`/`revert` can list and roll back changes.
                    // ResolveOrDefault: minimal containers may omit it, in which case mods simply have no history.
                    versionStore: c.ResolveOrDefault<ILuaScriptVersionStore>()),
                Lifetime.Singleton);
            builder.RegisterEntryPoint<ITickable>(c => new LuaModRuntimeTicker(
                    c.Resolve<LuaModRuntime>(),
                    c.ResolveOrDefault<IGameLogger>(),
                    c.ResolveOrDefault<IPublisher<LuaModEventEmitted>>()),
                Lifetime.Singleton);

            // Startup rehydration: reload every persisted active mod once the container is built.
            // The host's configured capability tier (scriptCapabilities) is the grant ceiling AND
            // the Full gate: a persisted Full mod regains Full across a restart only when the host
            // still has Full Lua enabled in this composition (inspector flag). A mod can never
            // exceed what the host currently grants, but a correctly-written persistent mod (e.g. a
            // day/night sun rotator using unity_*) keeps WORKING after a reload instead of silently
            // rehydrating without its APIs. With a Null/empty source store this is a harmless no-op.
            // Best-effort: a rehydrate failure is swallowed so it never aborts container construction.
            builder.RegisterBuildCallback(container =>
            {
                try
                {
                    // Permanent breadcrumb: which Lua tier this composition actually granted. Answers
                    // "why does my mod have no Full?" without a debugger (WebGL builds especially).
                    Logging.Log.Instance.Info(
                        $"[CoreAI] CoreAiMods: Lua capability grant = {scriptCapabilities} (enableFullLuaAccess={enableFullLuaAccess})");

                    // Play mode only: startup rehydration and the frame ticker are player-runtime
                    // behavior. EditMode containers (tests, tooling) share the REAL persistent mod
                    // store, so rehydrating there injects mods persisted by earlier runs into every
                    // fresh container ('already loaded' collisions); and DontDestroyOnLoad throws
                    // outside play mode. EditMode consumers load mods and call Tick explicitly.
                    if (Application.isPlaying)
                    {
                        LuaModRuntime runtime = container.Resolve<LuaModRuntime>();
                        runtime.RehydrateFromStore(scriptCapabilities,
                            (scriptCapabilities & LuaCapabilities.Full) != 0);

                        // Frame driver for mod timers/events. The ITickable entry-point registration
                        // above never dispatched (see LuaModRuntimeTickDriver docs), so hooks_every
                        // timers were frozen; a plain MonoBehaviour Update cannot fail that way.
                        GameObject tickerGo = new("CoreAI_LuaModTicker");
                        Object.DontDestroyOnLoad(tickerGo);
                        tickerGo.AddComponent<LuaModRuntimeTickDriver>().Initialize(runtime);
                    }
                }
                catch (VContainerException)
                {
                    // Minimal containers (tests, headless tools) may omit the mod runtime; rehydration
                    // is an additive convenience, not a requirement.
                }
            });

            // Native tool-calling path for the built-in Programmer role: the same sandbox and
            // game bindings as the Lua envelope pipeline, exposed as execute_lua + manage_mods
            // tools. Hosts that attach their own tools per role override via AgentMemoryPolicy.
            builder.Register<GameLuaToolExecutor>(Lifetime.Singleton)
                .As<LuaTool.ILuaExecutor>();
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
                        new LuaModsLlmTool(container.Resolve<LuaModRuntime>(), settings, log, scriptCapabilities));

                    // Full Lua reference on demand (read_skill) so the system prompt stays small.
                    // A Resources/AgentSkills/LuaModding TextAsset overrides the built-in text,
                    // same convention as the AgentPrompts/System overrides.
                    TextAsset skillOverride =
                        Resources.Load<TextAsset>("AgentSkills/LuaModding");
                    policy.AddSkillForRole(BuiltInAgentRoleIds.Programmer, SkillSet.FromTextContent(
                        BuiltInLuaModdingSkillText.SkillName,
                        BuiltInLuaModdingSkillText.SkillDescription,
                        skillOverride != null ? skillOverride.text : BuiltInLuaModdingSkillText.Instructions));
                }
                catch (VContainerException)
                {
                    // Minimal containers (tests, headless tools) may omit the orchestration
                    // services; the Lua tools are an additive convenience, not a requirement.
                }
            });
        }
    }
}
