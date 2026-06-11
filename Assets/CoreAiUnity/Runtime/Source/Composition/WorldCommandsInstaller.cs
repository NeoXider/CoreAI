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
        public static void RegisterWorldCommands(
            this IContainerBuilder builder,
            CoreAiPrefabRegistryAsset worldPrefabRegistry)
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
            builder.Register<CoreAiWorldLuaRuntimeBindings>(Lifetime.Singleton);
            builder.Register<LuaTimeBindings>(Lifetime.Singleton);
            builder.Register<CoreAiWorldQueryLuaBindings>(Lifetime.Singleton);
            // Factory registration: the ctor's optional budget parameters (int/long defaults)
            // are not resolvable by VContainer.
            builder.Register(c => new CoreAI.Ai.LuaLogicSlots(c.Resolve<CoreAI.Logging.ILog>()),
                Lifetime.Singleton);
            // Factory registration: the ctor's optional LuaCapabilities parameter (enum default)
            // is not resolvable by VContainer.
            builder.Register(c => new AggregatingGameLuaRuntimeBindings(
                    c.Resolve<CoreAI.Infrastructure.Logging.IGameLogger>(),
                    c.Resolve<CoreAiVersioningLuaRuntimeBindings>(),
                    c.Resolve<CoreAiWorldLuaRuntimeBindings>(),
                    c.Resolve<LuaTimeBindings>(),
                    c.Resolve<CoreAiWorldQueryLuaBindings>(),
                    c.Resolve<CoreAI.Ai.LuaLogicSlots>()), Lifetime.Singleton)
                .As<IGameLuaRuntimeBindings>();
            builder.Register<LoggingLuaExecutionObserver>(Lifetime.Singleton)
                .As<ILuaExecutionObserver>();
            builder.RegisterComponentOnNewGameObject<LuaCoroutineRunner>(Lifetime.Singleton,
                "CoreAI_LuaCoroutineRunner");

            // Persistent mod runtime: long-lived Lua mods with hooks/timers/events + per-mod store.
            builder.Register(c => new FileLuaModStore(), Lifetime.Singleton)
                .As<CoreAI.Ai.ILuaModStore>();
            builder.Register(c => new CoreAI.Ai.LuaModRuntime(
                    c.Resolve<IGameLuaRuntimeBindings>(),
                    c.Resolve<CoreAI.Ai.ILuaModStore>()),
                Lifetime.Singleton);
            builder.RegisterEntryPoint<LuaModRuntimeTicker>();
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