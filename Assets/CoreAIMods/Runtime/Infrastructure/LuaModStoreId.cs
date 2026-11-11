using System.IO;
using System;
using System.Text;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Maps an optional store id (a composition-level namespace) onto a file store's root directory.
    /// An empty id keeps the shared default root unchanged — the main game's persisted mods stay
    /// exactly where they are today. A non-empty id resolves to a <c>Stores/s-&lt;digest&gt;</c>
    /// subdirectory (<see cref="CoreAiStorageNames.StoreFolderName"/>), or to the <c>Stores/id-&lt;sha256&gt;</c>
    /// one an earlier build created when it exists, so compositions with different ids (e.g. demo scenes with
    /// different Lua tiers) persist and rehydrate fully isolated mod sets.
    /// </summary>
    public static class LuaModStoreId
    {
        private const string StoresFolderName = "Stores";

        /// <summary>
        /// Returns <paramref name="rootDirectory"/> untouched for an empty id, otherwise the
        /// id-specific subdirectory under it: the long-named one when an earlier build created it, else the short one.
        /// A lone UTF-16 surrogate in the id throws before any I/O (the strict encoder would otherwise alias
        /// distinct malformed ids).
        /// </summary>
        public static string ApplyTo(string rootDirectory, string storeId)
        {
            string id = storeId?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                return rootDirectory;
            }

            string legacy = Path.Combine(rootDirectory, StoresFolderName, CoreAiStorageNames.LegacyStoreFolderName(id));
            return Directory.Exists(legacy)
                ? legacy
                : Path.Combine(rootDirectory, StoresFolderName, CoreAiStorageNames.StoreFolderName(id));
        }

        /// <summary>Migrates a legacy namespace only after the host proves its exact identity; never merges or overwrites.</summary>
        public static bool TryMigrateLegacy(string rootDirectory, string storeId,
            Func<string, string, bool> verifyIdentity)
        {
            string id = storeId?.Trim();
            if (string.IsNullOrEmpty(id) || verifyIdentity == null)
            {
                return false;
            }

            string destination = Path.GetFullPath(ApplyTo(rootDirectory, id));
            string legacy = Path.GetFullPath(Path.Combine(rootDirectory, StoresFolderName, SanitizedFolderName(id)));
            if (!Directory.Exists(legacy) || Directory.Exists(destination) || !verifyIdentity(id, legacy))
            {
                return false;
            }

            Directory.Move(legacy, destination);
            if (!CoreAiWebGlPersistence.Sync())
            {
                throw new IOException("Legacy namespace was moved locally, but automatic persistence is not armed.");
            }

            return true;
        }

        /// <summary>
        /// Computes the legacy lossy name for explicitly verified migrations only.
        /// </summary>
        private static string SanitizedFolderName(string storeId)
        {
            StringBuilder safeBuilder = new(storeId.Length);
            foreach (char c in storeId)
            {
                bool allowed = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-';
                safeBuilder.Append(allowed ? c : '_');
            }

            string safe = safeBuilder.ToString();
            if (string.Equals(safe, storeId, System.StringComparison.Ordinal))
            {
                return safe;
            }

            uint hash = 2166136261u;
            foreach (char c in storeId)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return $"{safe}_{hash:x8}";
        }
    }
}
