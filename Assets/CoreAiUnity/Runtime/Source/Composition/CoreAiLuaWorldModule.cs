using System;
using CoreAI.Infrastructure.World;
using UnityEngine;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Optional child module that owns Lua and world-command configuration for a
    /// <see cref="CoreAILifetimeScope"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("CoreAI/Lua and World Commands Module")]
    public sealed class CoreAiLuaWorldModule : MonoBehaviour
    {
        [Tooltip("Prefab whitelist that Lua world commands are allowed to spawn.")]
        [SerializeField]
        private CoreAiPrefabRegistryAsset worldPrefabRegistry;

        [Tooltip("Scene names Lua may load via coreai_world_load_scene. Empty allows any Build Settings scene.")]
        [SerializeField]
        private string[] allowedScenes = Array.Empty<string>();

        [Tooltip("When enabled, Lua with Full capability can access arbitrary GameObjects/components via reflection.")]
        [SerializeField]
        private bool enableFullAccess;

        [Tooltip("When enabled, Full-tier Lua reflection can access non-public members.")]
        [SerializeField]
        private bool enableFullPrivateAccess;

        /// <summary>Prefab whitelist used by world commands.</summary>
        public CoreAiPrefabRegistryAsset WorldPrefabRegistry => worldPrefabRegistry;

        /// <summary>Scene whitelist used by Lua and native load-scene commands.</summary>
        public string[] AllowedScenes => allowedScenes ?? Array.Empty<string>();

        /// <summary>Whether the Full Lua capability tier is enabled.</summary>
        public bool FullAccessEnabled => enableFullAccess;

        /// <summary>Whether Full-tier Lua may access non-public members.</summary>
        public bool FullPrivateAccessEnabled => enableFullPrivateAccess;

        /// <summary>Registers this module's runtime services into its owning CoreAI scope.</summary>
        public void Register(IContainerBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            builder.RegisterWorldCommands(
                worldPrefabRegistry,
                AllowedScenes,
                enableFullAccess,
                enableFullPrivateAccess);
        }

        internal void ConfigureForMigration(
            CoreAiPrefabRegistryAsset prefabRegistry,
            string[] scenes,
            bool fullAccess,
            bool fullPrivateAccess)
        {
            worldPrefabRegistry = prefabRegistry;
            allowedScenes = scenes ?? Array.Empty<string>();
            enableFullAccess = fullAccess;
            enableFullPrivateAccess = fullPrivateAccess;
        }
    }
}
