using System.IO;
using System.Text;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Maps an optional store id (a composition-level namespace) onto a file store's root directory.
    /// An empty id keeps the shared default root unchanged — the main game's persisted mods stay
    /// exactly where they are today. A non-empty id resolves to a <c>Stores/&lt;sanitizedId&gt;</c>
    /// subdirectory, so compositions with different ids (e.g. demo scenes with different Lua tiers)
    /// persist and rehydrate fully isolated mod sets.
    /// </summary>
    public static class LuaModStoreId
    {
        private const string StoresFolderName = "Stores";

        /// <summary>
        /// Returns <paramref name="rootDirectory"/> untouched for an empty id, otherwise the
        /// id-specific subdirectory under it.
        /// </summary>
        public static string ApplyTo(string rootDirectory, string storeId)
        {
            string id = storeId?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                return rootDirectory;
            }

            return Path.Combine(rootDirectory, StoresFolderName, SanitizedFolderName(id));
        }

        /// <summary>
        /// Maps a raw store id to a unique, traversal-safe folder name. Characters outside
        /// <c>[A-Za-z0-9_-]</c> (which excludes dots and separators, so <c>..</c> cannot escape the
        /// root) are replaced with <c>_</c>; when the replacement changed anything a short hash of
        /// the raw id is appended so distinct ids cannot collide on the same folder.
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
