using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Persists a Lua mod's <em>source</em> plus its <see cref="LuaModManifest"/> so mods survive a
    /// restart and can be shared between hosts. This is deliberately separate from
    /// <see cref="ILuaModStore"/>: that interface is the per-mod runtime key/value scratch space
    /// backing <c>store_set</c>/<c>store_get</c>, whereas this one is the package store (the code and
    /// metadata that define the mod itself). A host wires an implementation (file system, player
    /// prefs, cloud, etc.).
    /// </summary>
    /// <remarks>
    /// The Lua-CSharp runtime persists best-effort: a failed or refused <see cref="Save"/> is logged and
    /// never aborts a load by itself. A world session is stricter, because a mod whose source is not
    /// kept would run now and be missing from every save and from the next start: its runtime facade
    /// checks, inside the load, that the store kept the source of every mod the load adds, and undoes
    /// the load (as a failed load, with its effects rolled back) when it did not. A store that must
    /// refuse a new mod, like the file store past the world-package mod limit
    /// (<c>RbxWorldPackageSerializer.MaximumMods</c>), throws from <see cref="Save"/>
    /// (<c>RbxWorldPackageFormatLimitException</c>, worded by
    /// <c>RbxWorldPackageFormatLimitException.DescribeModSourceLimit</c>), and implements
    /// <c>CoreAI.Mods.WorldPackages.ILuaModSourceAdmission</c> so the session refuses such a mod before
    /// it runs by the same rule; without it the session admits a new mod while the store lists fewer
    /// manifests than the limit.
    /// </remarks>
    public interface ILuaModSourceStore
    {
        /// <summary>
        /// Saves (creates or overwrites) the mod's source and manifest under <paramref name="id"/>. May
        /// throw to refuse keeping a new mod (see the remarks on this interface).
        /// </summary>
        void Save(string id, string source, LuaModManifest manifest);

        /// <summary>
        /// Loads a stored mod's source and manifest. Returns false (and null/empty out-params) when no
        /// package with this id exists.
        /// </summary>
        bool TryLoad(string id, out string source, out LuaModManifest manifest);

        /// <summary>
        /// Returns the manifests of every stored mod (active and dormant). A store that cannot be listed
        /// at all throws rather than answering an empty list: a load stamped meanwhile then records no
        /// load order instead of the first one, and a world capture fails instead of saving the world
        /// without its mods. (The built-in file store answers an empty listing marked unreadable, which
        /// those two callers treat the same way.)
        /// </summary>
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
