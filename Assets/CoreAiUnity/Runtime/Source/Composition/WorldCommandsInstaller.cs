using CoreAI;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Config;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
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
        public static void RegisterWorldCommands(
            this IContainerBuilder builder,
            CoreAiPrefabRegistryAsset worldPrefabRegistry,
            System.Collections.Generic.IEnumerable<string> allowedLuaScenes = null,
            bool enableFullLuaAccess = false)
        {
            CoreAiPrefabRegistryAsset registry =
                worldPrefabRegistry != null
                    ? worldPrefabRegistry
                    : ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
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
            builder.Register<LuaTimeBindings>(Lifetime.Singleton);
            builder.Register<CoreAiWorldQueryLuaBindings>(Lifetime.Singleton);
            builder.Register<CoreAiFullUnityLuaRuntimeBindings>(Lifetime.Singleton);
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
            builder.Register(c => new LuaModRuntime(
                    c.Resolve<IGameLuaRuntimeBindings>(),
                    c.Resolve<ILuaModStore>()),
                Lifetime.Singleton);
            builder.RegisterEntryPoint<LuaModRuntimeTicker>();

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
                        new LuaModsLlmTool(container.Resolve<LuaModRuntime>(), settings, log));
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
            builder.Register<CoreAiWorldCommandExecutor>(Lifetime.Singleton)
                .As<ICoreAiWorldCommandExecutor>();

            // Game Config: Unity SO-based config store
            builder.Register(c => new UnityGameConfigStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<IGameConfigStore>();
        }
    }
}