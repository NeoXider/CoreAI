using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Config;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Infrastructure.Lua;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Регистрация подсистемы World Commands: Lua bidings, execution observer, prefab registry, world executor.
    /// </summary>
    public static class WorldCommandsInstaller
    {
        /// <summary>
        /// Регистрирует все компоненты подсистемы мировых команд и Lua runtime.
        /// </summary>
        public static void RegisterWorldCommands(
            this IContainerBuilder builder,
            CoreAiPrefabRegistryAsset worldPrefabRegistry)
        {
            if (worldPrefabRegistry != null)
            {
                builder.RegisterInstance(worldPrefabRegistry);
            }
            else
            {
                builder.RegisterInstance(ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>());
            }

            builder.Register<DefaultDataOverlayPayloadValidator>(Lifetime.Singleton)
                .As<IDataOverlayPayloadValidator>();
            builder.Register<CoreAiVersioningLuaRuntimeBindings>(Lifetime.Singleton);
            builder.Register<CoreAiWorldLuaRuntimeBindings>(Lifetime.Singleton);
            builder.Register<LuaTimeBindings>(Lifetime.Singleton);
            builder.Register<AggregatingGameLuaRuntimeBindings>(Lifetime.Singleton)
                .As<IGameLuaRuntimeBindings>();
            builder.Register<LoggingLuaExecutionObserver>(Lifetime.Singleton)
                .As<ILuaExecutionObserver>();
            builder.RegisterComponentOnNewGameObject<LuaCoroutineRunner>(Lifetime.Singleton,
                "CoreAI_LuaCoroutineRunner");
            builder.Register<CoreAiWorldCommandExecutor>(Lifetime.Singleton)
                .As<ICoreAiWorldCommandExecutor>();

            // Game Config: Unity SO-based config store
            builder.Register(c => new UnityGameConfigStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<IGameConfigStore>();
        }
    }
}
