using CoreAI;
using CoreAI.Ai;
using CoreAI.Audit;
using CoreAI.Config;
using CoreAI.Features.Audit;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Config;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
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
        /// Optional scene whitelist for <c>load_scene</c> (native tool AND <c>coreai_world_load_scene</c>).
        /// <b>Security note:</b> null or empty is DELIBERATELY permissive — it does NOT block scene loads;
        /// any scene present/enabled in Build Settings stays loadable (legacy default). To actually
        /// restrict which scenes the model may load, pass a NON-EMPTY list; then only listed names pass and
        /// everything else is rejected. Pinned by CoreAiWorldCommandExecutorLoadSceneEditModeTests.
        /// </param>
        /// <param name="enableFullLuaAccess">
        /// Obsolete, ignored. It never granted the Full Lua tier: that grant is the
        /// <c>enableFullLuaAccess</c> argument of <c>CoreAiModsInstaller.RegisterCoreAiMods</c>
        /// (package <c>com.neoxider.coreaimods</c>), set in scenes by
        /// <c>CoreAiModsLifetimeScope.FullLuaAccessEnabled</c>.
        /// </param>
        /// <param name="enableFullLuaPrivateAccess">
        /// Obsolete, ignored. Full-tier private access is the <c>enableFullLuaPrivateAccess</c> argument of
        /// <c>CoreAiModsInstaller.RegisterCoreAiMods</c>, set in scenes by
        /// <c>CoreAiModsLifetimeScope.FullLuaPrivateAccessEnabled</c>.
        /// </param>
        public static void RegisterWorldCommands(
            this IContainerBuilder builder,
            CoreAiPrefabRegistryAsset worldPrefabRegistry,
            System.Collections.Generic.IEnumerable<string> allowedLuaScenes = null,
            bool enableFullLuaAccess = false,
            bool enableFullLuaPrivateAccess = false)
        {
            // WHY: the two Full-tier flags stay in the signature only because existing callers compile
            // against it (named arguments included). This package cannot reference the Lua mods package
            // that owns the tier, so nothing here can honour them; CoreAiModsInstaller reads its own copies.
            RegisterWorldCommandsCore(builder, worldPrefabRegistry, allowedLuaScenes, null);
        }

        /// <summary>
        /// Registers world commands and selects the file the <see cref="WorldStateManager"/> restores from,
        /// auto-saves to and resets. Use it to keep two worlds (save slots, server rooms, a test fixture)
        /// from sharing one snapshot without replacing the manager's registration.
        /// </summary>
        /// <param name="builder">Container builder.</param>
        /// <param name="worldPrefabRegistry">Prefab whitelist for Lua spawn commands.</param>
        /// <param name="allowedLuaScenes">
        /// Scene whitelist for <c>load_scene</c>; null or empty allows any Build Settings scene, as in the
        /// other overload.
        /// </param>
        /// <param name="worldStateSnapshotPath">
        /// Absolute snapshot file. Null keeps the default, <c>CoreAI/WorldState/world_state.json</c> under
        /// <see cref="CoreAiStorageRoot.PersistentDataRoot"/>. Any other value is validated here, so an empty,
        /// relative, traversal or directory path fails at registration rather than at first resolve.
        /// </param>
        /// <exception cref="System.ArgumentException">The snapshot path is not an absolute file path.</exception>
        public static void RegisterWorldCommands(
            this IContainerBuilder builder,
            CoreAiPrefabRegistryAsset worldPrefabRegistry,
            System.Collections.Generic.IEnumerable<string> allowedLuaScenes,
            string worldStateSnapshotPath)
        {
            string snapshotPath = worldStateSnapshotPath == null
                ? null
                : WorldStateManager.NormalizeSaveFilePath(worldStateSnapshotPath);
            RegisterWorldCommandsCore(builder, worldPrefabRegistry, allowedLuaScenes, snapshotPath);
        }

        private static void RegisterWorldCommandsCore(
            IContainerBuilder builder,
            CoreAiPrefabRegistryAsset worldPrefabRegistry,
            System.Collections.Generic.IEnumerable<string> allowedLuaScenes,
            string worldStateSnapshotPath)
        {
            CoreAiPrefabRegistryAsset registry;
            if (worldPrefabRegistry != null)
            {
                registry = worldPrefabRegistry;
            }
            else
            {
                // WHY: No inspector-assigned registry: create a throwaway one. ScriptableObjects are not
                // garbage-collected, so register a container-owned disposable that destroys it on scope
                // teardown instead of leaking one instance per container build (scene reload / play-mode).
                registry = ScriptableObject.CreateInstance<CoreAiPrefabRegistryAsset>();
                registry.hideFlags = HideFlags.DontSave;
                CoreAiPrefabRegistryAsset autoCreated = registry;
                builder.Register(_ => new AutoCreatedPrefabRegistryOwner(autoCreated), Lifetime.Singleton)
                    .AsSelf();
                builder.RegisterBuildCallback(container =>
                {
                    // WHY: Force instantiation so the container tracks it and disposes it on teardown.
                    container.Resolve<AutoCreatedPrefabRegistryOwner>();
                });
            }

            builder.RegisterInstance<ICoreAiPrefabRegistry, CoreAiPrefabRegistryAsset>(registry);

            // WHY: Factory registration so the load_scene whitelist (allowedLuaScenes) reaches the executor.
            // Enforcing it here makes the native world_command tool honour the same restriction as the
            // Lua coreai_world_load_scene binding, instead of the native path bypassing it.
            builder.Register(c =>
                    {
                        CoreAiWorldCommandExecutor inner = new(
                            c.Resolve<IGameLogger>(),
                            c.Resolve<ICoreAiPrefabRegistry>(),
                            allowedLuaScenes,
                            c.ResolveOrDefault<ICoreAISettings>()?.AllowWorldPrimitives ?? true);
                        IAuditLog audit = c.ResolveOrDefault<IAuditLog>();
                        return audit != null
                            ? (ICoreAiWorldCommandExecutor)new AuditedWorldCommandExecutor(inner, audit)
                            : inner;
                    },
                    Lifetime.Singleton)
                .As<ICoreAiWorldCommandExecutor>();

            builder.Register(c => new CoreAiComponentCommandExecutor(c.Resolve<IGameLogger>(),
                    c.ResolveOrDefault<CoreAiComponentCommandOptions>()), Lifetime.Singleton)
                .As<ICoreAiComponentCommandExecutor>();

            builder.Register(c => new UnityGameConfigStore(c.Resolve<IGameLogger>()), Lifetime.Singleton)
                .As<IGameConfigStore>();

            builder.Register(c =>
                    {
                        IGameLogger logger = c.Resolve<IGameLogger>();
                        ICoreAiPrefabRegistry prefabs = c.Resolve<ICoreAiPrefabRegistry>();
                        bool allowPrimitives = c.ResolveOrDefault<ICoreAISettings>()?.AllowWorldPrimitives ?? true;
                        return worldStateSnapshotPath == null
                            ? new WorldStateManager(logger, prefabs, allowPrimitives)
                            : new WorldStateManager(logger, prefabs, allowPrimitives, worldStateSnapshotPath);
                    },
                    Lifetime.Singleton)
                .As<IWorldStateManager>()
                .As<IWorldStateWriteResults>()
                .AsSelf();

            RegisterAgentVision(builder);
            RegisterWorldBuildingRolesTool(builder);
            RegisterInventoryProfileTool(builder);
        }

        /// <summary>
        /// Registers the native <c>world_command</c> tool (<see cref="WorldLlmTool"/>) into the
        /// <see cref="BuiltInToolProfileIds.WorldBuilder"/> profile, which the built-in scene-building roles
        /// (<see cref="BuiltInAgentRoleIds.Creator"/> and <see cref="BuiltInAgentRoleIds.Builder"/>) reference.
        /// Both roles' system prompts tell the model to place objects with this tool. The Programmer role
        /// builds through the Lua/Rbx surface instead (see <c>CoreAiModsInstaller</c>).
        /// </summary>
        private static void RegisterWorldBuildingRolesTool(IContainerBuilder builder)
        {
            builder.RegisterBuildCallback(container =>
            {
                try
                {
                    AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();
                    ICoreAiWorldCommandExecutor executor = container.Resolve<ICoreAiWorldCommandExecutor>();
                    ICoreAISettings settings = container.Resolve<ICoreAISettings>();
                    IGameLogger logger = container.Resolve<IGameLogger>();

                    // WHY a factory: each role of the profile gets its own lightweight instance, as before. The
                    // profile replaces an entry by name, so a second pass (Play-Mode re-entry, a rebuilt or
                    // shared policy across scopes) still leaves one world_command per role.
                    policy.AddToolToProfile(BuiltInToolProfileIds.WorldBuilder, "world_command",
                        _ => new WorldLlmTool(executor, settings, logger));
                }
                catch (VContainerException)
                {
                    // WHY: Minimal containers (tests, headless tools) may omit the orchestration services;
                    // the world tool is an additive convenience, not a requirement.
                }
            });
        }

        /// <summary>
        /// Registers <c>get_inventory</c> (<see cref="InventoryLlmTool"/>) into the
        /// <see cref="BuiltInToolProfileIds.Inventory"/> profile, which the built-in Merchant role references,
        /// when the container holds an <see cref="InventoryTool.IInventoryProvider"/>. Without a provider nothing
        /// is registered and the Merchant prompt's inventory instruction stays conditional on the tool.
        /// </summary>
        private static void RegisterInventoryProfileTool(IContainerBuilder builder)
        {
            builder.RegisterBuildCallback(container =>
            {
                try
                {
                    InventoryTool.IInventoryProvider provider =
                        container.ResolveOrDefault<InventoryTool.IInventoryProvider>();
                    if (provider == null)
                    {
                        return;
                    }

                    container.Resolve<AgentMemoryPolicy>()
                        .AddToolToProfile(BuiltInToolProfileIds.Inventory, new InventoryLlmTool(provider));
                }
                catch (VContainerException)
                {
                    // WHY: Minimal containers (tests, headless tools) may omit the orchestration services.
                }
            });
        }

        /// <summary>
        /// Registers the agent-vision blocks (<see cref="Vision.AgentCameraOptions"/>,
        /// <see cref="Vision.ICameraFrameReadback"/>, <see cref="Vision.IAgentCameraRig"/>), the service built from
        /// them, and the <c>camera</c> tool (<c>camera_capture</c>/<c>camera_look</c>/<c>camera_orbit</c>/
        /// <c>camera_list</c>) into the <see cref="BuiltInToolProfileIds.Vision"/> profile, which the built-in
        /// Programmer role references. A block registered before this call is kept, so a game configures or
        /// replaces one without touching the others. Capture is always read-only-safe on any camera; movement is
        /// gated by the opt-in <see cref="CoreAI.Vision.CoreAiAgentCamera"/> marker (or the free agent camera) so
        /// the player's camera is never hijacked. See <c>Docs/CoreAI/agent-vision.md</c>. SmartChat is
        /// intentionally not given the profile by default (hosts can add it via
        /// <see cref="AgentMemoryPolicy.SetToolProfilesForRole"/> or <see cref="AgentMemoryPolicy.AddToolForRole"/>).
        /// </summary>
        internal static void RegisterAgentVision(IContainerBuilder builder)
        {
            if (!builder.Exists(typeof(Vision.AgentCameraOptions)))
            {
                builder.RegisterInstance(Vision.AgentCameraOptions.CreateDefault());
            }

            if (!builder.Exists(typeof(Vision.ICameraFrameReadback), true))
            {
                builder.Register<Vision.ICameraFrameReadback>(_ => Vision.CameraFrameReadback.CreateDefault(),
                    Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(Vision.IAgentCameraRig), true))
            {
                builder.Register<Vision.IAgentCameraRig>(
                    r => new Vision.AgentCameraRig(r.Resolve<Vision.AgentCameraOptions>(), null),
                    Lifetime.Singleton);
            }

            if (!builder.Exists(typeof(Vision.IAgentCameraService), true))
            {
                // WHY a factory: the clock argument is not container-resolvable; null keeps the monotonic default.
                builder.Register(r => new Vision.AgentCameraService(
                            r.Resolve<Vision.AgentCameraOptions>(),
                            r.Resolve<Vision.ICameraFrameReadback>(),
                            r.Resolve<Vision.IAgentCameraRig>(),
                            null),
                        Lifetime.Singleton)
                    .As<Vision.IAgentCameraService>();
            }

            builder.RegisterBuildCallback(container =>
            {
                try
                {
                    AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();
                    Vision.IAgentCameraService cameraService =
                        container.Resolve<Vision.IAgentCameraService>();
                    policy.AddToolToProfile(BuiltInToolProfileIds.Vision, "camera",
                        roleId => new CoreAI.Vision.CameraLlmTool(cameraService, roleId));
                }
                catch (VContainerException)
                {
                    // WHY: Minimal containers (tests, headless tools) may omit the orchestration services; the
                    // camera tool is an additive convenience, not a requirement.
                }
            });
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
                if (_asset == null)
                {
                    return;
                }

                // WHY both: an edit-mode scope (a test, an editor tool) is disposed outside Play Mode, where
                // Object.Destroy is refused with an error log and the asset would leak.
                if (Application.isPlaying)
                {
                    Object.Destroy(_asset);
                }
                else
                {
                    Object.DestroyImmediate(_asset);
                }

                _asset = null;
            }
        }
    }
}
