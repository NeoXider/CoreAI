using CoreAI;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Config;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Messaging;
using CoreAI.Infrastructure.World;
using MessagePipe;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Registers world-command routing and execution services in the DI container.
    /// </summary>
    public static class WorldCommandsInstaller
    {
        /// <summary>
        /// Registers world commands.
        /// </summary>
        /// <param name="builder">Container builder.</param>
        /// <param name="worldPrefabRegistry">Prefab whitelist for Lua spawn commands.</param>
        /// <param name="allowedLuaScenes">
        /// Optional whitelist for <c>coreai_world_load_scene</c>. When null or empty any scene from
        /// Build Settings stays loadable (legacy behavior); otherwise only listed names pass.
        /// </param>
        /// <param name="enableFullLuaAccess">
        /// When true, scripts with the Full capability tier receive reflection bindings to arbitrary
        /// GameObjects/components (<see cref="CoreAiFullUnityLuaRuntimeBindings"/>). Off by default.
        /// </param>
        /// <param name="enableFullLuaPrivateAccess">
        /// When true, Full-tier Lua reflection may access non-public members. Off by default.
        /// </param>
        public static void RegisterWorldCommands(
            this IContainerBuilder builder,
            CoreAiPrefabRegistryAsset worldPrefabRegistry,
            System.Collections.Generic.IEnumerable<string> allowedLuaScenes = null,
            bool enableFullLuaAccess = false,
            bool enableFullLuaPrivateAccess = false,
            IFullLuaAccessBlacklistPolicy fullLuaBlacklistPolicy = null)
        {
            CoreAiPrefabRegistryAsset registry;
            if (worldPrefabRegistry != null)
            {
                registry = worldPrefabRegistry;
            }
            else
            {
                // No inspector-assigned registry: create a throwaway one. ScriptableObjects are not
                // garbage-collected, so register a container-owned disposable that destroys it on scope
                // teardown instead of leaking one instance per container build (scene reload / play-mode).
                registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
                registry.hideFlags = HideFlags.DontSave;
                CoreAiPrefabRegistryAsset autoCreated = registry;
                builder.Register(_ => new AutoCreatedPrefabRegistryOwner(autoCreated), Lifetime.Singleton)
                    .AsSelf();
                builder.RegisterBuildCallback(container =>
                {
                    // Force instantiation so the container tracks it and disposes it on teardown.
                    container.Resolve<AutoCreatedPrefabRegistryOwner>();
                });
            }

            builder.RegisterInstance<ICoreAiPrefabRegistry, CoreAiPrefabRegistryAsset>(registry);

            builder.Register<DefaultDataOverlayPayloadValidator>(Lifetime.Singleton)
                .As<IDataOverlayPayloadValidator>();
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
            builder.Register<CoreAiVersioningLuaRuntimeBindings>(Lifetime.Singleton);
            // Factory registration: the ctor's optional scene whitelist is supplied by the host
            // (e.g. CoreAILifetimeScope inspector), not resolvable by VContainer.
            builder.Register(c => new CoreAiWorldLuaRuntimeBindings(
                    c.Resolve<Messaging.IAiGameCommandSink>(),
                    allowedLuaScenes),
                Lifetime.Singleton);
            builder.Register(c => new CoreAiComponentLuaRuntimeBindings(
                    c.Resolve<Messaging.IAiGameCommandSink>()),
                Lifetime.Singleton);
            builder.Register<LuaTimeBindings>(Lifetime.Singleton);
            builder.Register<CoreAiWorldQueryLuaBindings>(Lifetime.Singleton);
            builder.Register(c => new CoreAiFullUnityLuaRuntimeBindings(
                    c.Resolve<IGameLogger>(),
                    enableFullLuaPrivateAccess,
                    fullLuaBlacklistPolicy),
                Lifetime.Singleton);
            LuaCapabilities scriptCapabilities = enableFullLuaAccess
                ? LuaCapabilities.All | LuaCapabilities.Full
                : LuaCapabilities.All;
            // Factory registration: the ctor's optional budget parameters (int/long defaults)
            // are not resolvable by VContainer.
            builder.Register(c => new LuaLogicSlots(c.Resolve<Logging.ILog>()),
                Lifetime.Singleton);
            // Factory registration: the ctor's optional LuaCapabilities parameter (enum default)
            // is not resolvable by VContainer.
            builder.Register(c => new AggregatingGameLuaRuntimeBindings(
                    c.Resolve<IGameLogger>(),
                    c.Resolve<CoreAiVersioningLuaRuntimeBindings>(),
                    c.Resolve<CoreAiWorldLuaRuntimeBindings>(),
                    c.Resolve<CoreAiComponentLuaRuntimeBindings>(),
                    c.Resolve<LuaTimeBindings>(),
                    c.Resolve<CoreAiWorldQueryLuaBindings>(),
                    c.Resolve<LuaLogicSlots>(),
                    c.Resolve<CoreAiFullUnityLuaRuntimeBindings>(),
                    scriptCapabilities), Lifetime.Singleton)
                .As<IGameLuaRuntimeBindings>();
            builder.Register<LoggingLuaExecutionObserver>(Lifetime.Singleton)
                .As<ILuaExecutionObserver>();
            builder.RegisterComponentOnNewGameObject<LuaCoroutineRunner>(Lifetime.Singleton,
                "CoreAI_LuaCoroutineRunner");

            // Persistent mod runtime: long-lived Lua mods with hooks/timers/events + per-mod store.
            builder.Register(c => new FileLuaModStore(), Lifetime.Singleton)
                .As<ILuaModStore>();
            // Package store: persists each mod's source + manifest so active mods survive a restart
            // (rehydrated at startup) and can be exported/imported between hosts.
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
                    UnityEngine.Debug.Log(
                        $"[CoreAI] WorldCommands: Lua capability grant = {scriptCapabilities} (enableFullLuaAccess={enableFullLuaAccess})");
                    LuaModRuntime runtime = container.Resolve<LuaModRuntime>();
                    runtime.RehydrateFromStore(scriptCapabilities,
                        allowFull: (scriptCapabilities & LuaCapabilities.Full) != 0);

                    // Frame driver for mod timers/events. The ITickable entry-point registration
                    // below never dispatched (see LuaModRuntimeTickDriver docs), so hooks_every
                    // timers were frozen; a plain MonoBehaviour Update cannot fail that way.
                    var tickerGo = new UnityEngine.GameObject("CoreAI_LuaModTicker");
                    UnityEngine.Object.DontDestroyOnLoad(tickerGo);
                    tickerGo.AddComponent<CoreAI.Infrastructure.Lua.LuaModRuntimeTickDriver>().Initialize(runtime);
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
                }
                catch (VContainerException)
                {
                    // Minimal containers (tests, headless tools) may omit the orchestration
                    // services; the Lua tools are an additive convenience, not a requirement.
                }
            });
#else
            builder.Register<CoreAI.Ai.CoreDefaultLuaRuntimeBindings>(Lifetime.Singleton)
                .As<CoreAI.Ai.IGameLuaRuntimeBindings>();
            builder.Register<CoreAI.Ai.NullLuaExecutionObserver>(Lifetime.Singleton)
                .As<CoreAI.Ai.ILuaExecutionObserver>();
#endif
            // Factory registration so the load_scene whitelist (allowedLuaScenes) reaches the executor.
            // Enforcing it here makes the native world_command tool honour the same restriction as the
            // Lua coreai_world_load_scene binding, instead of the native path bypassing it.
            builder.Register(c => new CoreAiWorldCommandExecutor(
                    c.Resolve<IGameLogger>(),
                    c.Resolve<ICoreAiPrefabRegistry>(),
                    allowedLuaScenes,
                    c.ResolveOrDefault<ICoreAISettings>()?.AllowWorldPrimitives ?? true),
                Lifetime.Singleton)
                .As<ICoreAiWorldCommandExecutor>();

            builder.Register(c => new CoreAiComponentCommandExecutor(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<ICoreAiComponentCommandExecutor>();

            // Game Config: Unity SO-based config store
            builder.Register(c => new UnityGameConfigStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<IGameConfigStore>();
        }

        /// <summary>
        /// Container-owned holder that destroys an auto-created <see cref="CoreAiPrefabRegistryAsset"/>
        /// (one with no inspector-assigned asset) when the DI scope is disposed, so the ScriptableObject
        /// is not leaked across scope rebuilds.
        /// </summary>
        private sealed class AutoCreatedPrefabRegistryOwner : System.IDisposable
        {
            private CoreAiPrefabRegistryAsset _asset;

            public AutoCreatedPrefabRegistryOwner(CoreAiPrefabRegistryAsset asset)
            {
                _asset = asset;
            }

            public void Dispose()
            {
                if (_asset != null)
                {
                    UnityEngine.Object.Destroy(_asset);
                    _asset = null;
                }
            }
        }
    }
}
