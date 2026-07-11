using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// No-op <see cref="ILuaModSourceStore"/> used when a host wires no persistence: mods stay
    /// in-memory only (the pre-persistence behaviour). Save/SetActive/Delete do nothing, TryLoad
    /// reports absence, and List is empty. <c>LuaModRuntime</c> defaults to
    /// <see cref="Instance"/> so persistence calls are always safe to make unconditionally.
    /// </summary>
    public sealed class NullLuaModSourceStore : ILuaModSourceStore
    {
        /// <summary>Shared stateless singleton.</summary>
        public static readonly NullLuaModSourceStore Instance = new();

        private static readonly IReadOnlyList<LuaModManifest> Empty = new LuaModManifest[0];

        private NullLuaModSourceStore()
        {
        }

        /// <inheritdoc />
        public void Save(string id, string source, LuaModManifest manifest)
        {
        }

        /// <inheritdoc />
        public bool TryLoad(string id, out string source, out LuaModManifest manifest)
        {
            source = "";
            manifest = null;
            return false;
        }

        /// <inheritdoc />
        public IReadOnlyList<LuaModManifest> List()
        {
            return Empty;
        }

        /// <inheritdoc />
        public void SetActive(string id, bool active)
        {
        }

        /// <inheritdoc />
        public void Delete(string id)
        {
        }
    }
}
