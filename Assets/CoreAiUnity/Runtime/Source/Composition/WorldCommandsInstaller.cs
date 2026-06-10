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
            builder.Register<AggregatingGameLuaRuntimeBindings>(Lifetime.Singleton)
                .As<IGameLuaRuntimeBindings>();
            builder.Register<LoggingLuaExecutionObserver>(Lifetime.Singleton)
                .As<ILuaExecutionObserver>();
            builder.RegisterComponentOnNewGameObject<LuaCoroutineRunner>(Lifetime.Singleton,
                "CoreAI_LuaCoroutineRunner");
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