using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Persists a Lua mod's <em>source</em> plus its <see cref="LuaModManifest"/> so mods survive a
    /// restart and can be shared between hosts. This is deliberately separate from
    /// <see cref="ILuaModStore"/>: that interface is the per-mod runtime key/value scratch space
    /// backing <c>store_set</c>/<c>store_get</c>, whereas this one is the package store (the code and
    /// metadata that define the mod itself). A host wires an implementation (file system, player
    /// prefs, cloud, etc.); <c>LuaModRuntime</c> calls it best-effort and never lets a store
    /// failure abort a load.
    /// </summary>
    public interface ILuaModSourceStore
    {
        /// <summary>
        /// Saves (creates or overwrites) the mod's source and manifest under <paramref name="id"/>.
        /// </summary>
        void Save(string id, string source, LuaModManifest manifest);

        /// <summary>
        /// Loads a stored mod's source and manifest. Returns false (and null/empty out-params) when no
        /// package with this id exists.
        /// </summary>
        bool TryLoad(string id, out string source, out LuaModManifest manifest);

        /// <summary>Returns the manifests of every stored mod (active and dormant).</summary>
        IReadOnlyList<LuaModManifest> List();

        /// <summary>
        /// Flips the persisted <see cref="LuaModManifest.Active"/> flag without touching the source, so
        /// an unloaded mod stays on disk but does not auto-reload on the next rehydrate. No-op when the
        /// id is unknown.
        /// </summary>
        void SetActive(string id, bool active);

        /// <summary>Permanently removes the stored package (source and manifest). No-op when absent.</summary>
        void Delete(string id);
    }
}
