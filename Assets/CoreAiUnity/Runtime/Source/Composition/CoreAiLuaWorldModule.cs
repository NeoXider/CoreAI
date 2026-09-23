using System;
using CoreAI.Infrastructure.World;
using UnityEngine;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Optional child module that owns world-command configuration for a
    /// <see cref="CoreAILifetimeScope"/>: the prefab whitelist and the scene whitelist the
    /// world-command executor enforces.
    /// <para>
    /// It grants no Lua capability tier. The Full Lua tier and its private-member access are granted only
    /// by <c>CoreAiModsLifetimeScope</c> (package <c>com.neoxider.coreaimods</c>, which this package
    /// cannot reference).
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("CoreAI/Lua and World Commands Module")]
    public sealed class CoreAiLuaWorldModule : MonoBehaviour
    {
        private const string FullAccessObsoleteMessage =
            "Never granted the Full Lua tier. Use CoreAiModsLifetimeScope.FullLuaAccessEnabled " +
            "(com.neoxider.coreaimods), the only flag execute_lua and manage_mods honour.";

        private const string FullPrivateAccessObsoleteMessage =
            "Never granted Full-tier private access. Use CoreAiModsLifetimeScope.FullLuaPrivateAccessEnabled " +
            "(com.neoxider.coreaimods), the only flag execute_lua and manage_mods honour.";

        [Tooltip("Prefab whitelist that world commands (native world_command and Lua) are allowed to spawn.")]
        [SerializeField]
        private CoreAiPrefabRegistryAsset worldPrefabRegistry;

        [Tooltip("Scene names the world-command executor may load, for native world_command load_scene and " +
                 "for Lua coreai_world_load_scene. Empty allows any Build Settings scene.")]
        [SerializeField]
        private string[] allowedScenes = Array.Empty<string>();

        // WHY: still serialized so existing scenes deserialize without data loss; hidden because nothing
        // grants the Full tier from it. The live switch is CoreAiModsLifetimeScope.enableFullLuaAccess.
        [Tooltip("Deprecated, no effect. The Full Lua tier is granted by CoreAiModsLifetimeScope.")]
        [HideInInspector]
        [SerializeField]
        private bool enableFullAccess;

        // WHY: same as enableFullAccess. The live switch is CoreAiModsLifetimeScope.enableFullLuaPrivateAccess.
        [Tooltip("Deprecated, no effect. Full-tier private access is granted by CoreAiModsLifetimeScope.")]
        [HideInInspector]
        [SerializeField]
        private bool enableFullPrivateAccess;

        /// <summary>Prefab whitelist used by world commands.</summary>
        public CoreAiPrefabRegistryAsset WorldPrefabRegistry => worldPrefabRegistry;

        /// <summary>
        /// Scene whitelist the world-command executor enforces for every <c>load_scene</c> it runs,
        /// whether it came from the native tool or from Lua.
        /// </summary>
        public string[] AllowedScenes => allowedScenes ?? Array.Empty<string>();

        /// <summary>
        /// Legacy serialized value that never granted anything. The Full Lua tier is granted only by
        /// <c>CoreAiModsLifetimeScope.FullLuaAccessEnabled</c>.
        /// </summary>
        [Obsolete(FullAccessObsoleteMessage)]
        public bool FullAccessEnabled => enableFullAccess;

        /// <summary>
        /// Legacy serialized value that never granted anything. Full-tier private access is granted only by
        /// <c>CoreAiModsLifetimeScope.FullLuaPrivateAccessEnabled</c>.
        /// </summary>
        [Obsolete(FullPrivateAccessObsoleteMessage)]
        public bool FullPrivateAccessEnabled => enableFullPrivateAccess;

        /// <summary>Registers this module's runtime services into its owning CoreAI scope.</summary>
        public void Register(IContainerBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            builder.RegisterWorldCommands(worldPrefabRegistry, AllowedScenes);
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
