using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances.DataStores;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// The default data of a game composition: <see cref="FileRbxDataStorePersistence"/> under the storage root, the
    /// composition's mod store id as the game namespace, and the limits of the platform the player runs on.
    /// </summary>
    public static class CoreAiDataStoreDefaults
    {
        /// <summary><see cref="RbxDataStoreOptions.WebGl"/> in a WebGL player, <see cref="RbxDataStoreOptions.Desktop"/> elsewhere.</summary>
        public static RbxDataStoreOptions ForThisPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return RbxDataStoreOptions.WebGl();
#else
            return RbxDataStoreOptions.Desktop();
#endif
        }

        /// <summary>The backend a composition registers when the host gave none.</summary>
        /// <param name="modStoreId">The composition's mod store id ("" for the main game); it is the game namespace.</param>
        /// <param name="log">Where storage problems are reported; may be null.</param>
        /// <param name="options">The limits; null uses <see cref="ForThisPlatform"/>.</param>
        public static RbxDataStoreBackend CreateBackend(string modStoreId, ILog log = null, RbxDataStoreOptions options = null)
        {
            return new RbxDataStoreBackend(new FileRbxDataStorePersistence(), modStoreId, options ?? ForThisPlatform(),
                report: problem => log?.Error(problem));
        }
    }
}
