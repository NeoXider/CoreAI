using CoreAI;
using CoreAI.Ai;
using CoreAI.Config;
using CoreAI.Infrastructure.Config;
using CoreAI.Infrastructure.Logging;
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
            bool enableFullLuaPrivateAccess = false)
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

            builder.Register(c => new UnityGameConfigStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<IGameConfigStore>();

            builder.Register(c => new WorldStateManager(
                        c.Resolve<IGameLogger>(),
                        c.Resolve<ICoreAiPrefabRegistry>(),
                        c.ResolveOrDefault<ICoreAISettings>()?.AllowWorldPrimitives ?? true),
                    Lifetime.Singleton)
                .As<IWorldStateManager>()
                .AsSelf();
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
                    Object.Destroy(_asset);
                    _asset = null;
                }
            }
        }
    }
}