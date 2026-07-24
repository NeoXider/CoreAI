using CoreAI;
using CoreAI.Ai;
using CoreAI.Audit;
using CoreAI.Config;
using CoreAI.Features.Audit;
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
        /// When true, scripts with the Full capability tier receive reflection bindings to arbitrary
        /// GameObjects/components (the Lua-CSharp <c>LuaCsFullUnityRuntimeBindings</c>). Off by default.
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

            RegisterAgentVision(builder);
            RegisterWorldBuildingRolesTool(builder);
        }

        /// <summary>
        /// Attaches the native <c>world_command</c> tool (<see cref="WorldLlmTool"/>) to the built-in
        /// scene-building chat roles (<see cref="BuiltInAgentRoleIds.Creator"/> and
        /// <see cref="BuiltInAgentRoleIds.Builder"/>). Both roles' system prompts
        /// (<see cref="CoreAI.Ai.BuiltInAgentSystemPromptTexts.Creator"/> /
        /// <see cref="CoreAI.Ai.BuiltInAgentSystemPromptTexts.Builder"/>) tell the model to place objects
        /// with this tool; without the wiring the roles had no way to build and fell back to text/memory.
        /// The Programmer role builds through the Lua/Rbx surface instead (see <c>CoreAiModsInstaller</c>)
        /// and is intentionally left unchanged.
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

                    // WHY: a fresh tool instance per role — WorldLlmTool is lightweight and AddToolForRole
                    // keeps a per-role list, so sharing state across roles is neither needed nor desirable.
                    // The contains-guard keeps the registration idempotent: a duplicate world_command name
                    // resolves as ambiguous downstream, so a second pass (Play-Mode re-entry, a rebuilt or
                    // shared policy across scopes) must not append it twice.
                    AddWorldToolIfMissing(policy, BuiltInAgentRoleIds.Creator, executor, settings, logger);
                    AddWorldToolIfMissing(policy, BuiltInAgentRoleIds.Builder, executor, settings, logger);
                }
                catch (VContainerException)
                {
                    // WHY: Minimal containers (tests, headless tools) may omit the orchestration services;
                    // the world tool is an additive convenience, not a requirement.
                }
            });
        }

        /// <summary>
        /// Adds a fresh <see cref="WorldLlmTool"/> to <paramref name="roleId"/> only when that role does
        /// not already expose <c>world_command</c>, so repeated registration never produces the ambiguous
        /// duplicate tool name that would break tool resolution.
        /// </summary>
        private static void AddWorldToolIfMissing(
            AgentMemoryPolicy policy,
            string roleId,
            ICoreAiWorldCommandExecutor executor,
            ICoreAISettings settings,
            IGameLogger logger)
        {
            foreach (ILlmTool existing in policy.GetToolsForRole(roleId))
            {
                if (existing != null && existing.Name == "world_command")
                {
                    return;
                }
            }

            policy.AddToolForRole(roleId, new WorldLlmTool(executor, settings, logger));
        }

        /// <summary>
        /// Registers the agent-vision service and attaches the <c>camera</c> tool
        /// (<c>camera_capture</c>/<c>camera_look</c>/<c>camera_list</c>) to the built-in Programmer role.
        /// Capture is always read-only-safe on any camera; movement is gated by the opt-in
        /// <see cref="CoreAI.Vision.CoreAiAgentCamera"/> marker so the player's camera is never hijacked.
        /// See <c>Docs/CoreAI/agent-vision.md</c>. SmartChat is intentionally not registered by default
        /// (hosts can add it via <see cref="AgentMemoryPolicy.AddToolForRole"/>).
        /// </summary>
        private static void RegisterAgentVision(IContainerBuilder builder)
        {
            // WHY: Factory lambda: the service's clock/rate-limit ctor args are not container-resolvable, so
            // construct with its production defaults (Stopwatch clock, 1s capture rate limit).
            builder.Register(_ => new Vision.AgentCameraService(), Lifetime.Singleton)
                .As<Vision.IAgentCameraService>();

            builder.RegisterBuildCallback(container =>
            {
                try
                {
                    AgentMemoryPolicy policy = container.Resolve<AgentMemoryPolicy>();
                    Vision.IAgentCameraService cameraService =
                        container.Resolve<Vision.IAgentCameraService>();
                    policy.AddToolForRole(BuiltInAgentRoleIds.Programmer,
                        new CoreAI.Vision.CameraLlmTool(cameraService, BuiltInAgentRoleIds.Programmer));
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
                if (_asset != null)
                {
                    Object.Destroy(_asset);
                    _asset = null;
                }
            }
        }
    }
}
