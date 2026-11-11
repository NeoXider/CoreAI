namespace CoreAI.Mods.Folders
{
    /// <summary>
    /// What the folder tree and the folder operations need to know about one mod: its id, display name,
    /// stored folder (the raw <c>category</c>, read leniently), tags for search, whether it is on, and its
    /// recorded load order. <see cref="Payload"/> carries the host's own record (for example the Hub's mod
    /// record) through the tree to the view untouched.
    /// </summary>
    public sealed class ModFolderModEntry
    {
        /// <param name="id">Stable mod id; entries with a blank id are ignored by the tree.</param>
        /// <param name="name">Display name; blank falls back to the id.</param>
        /// <param name="folder">The stored category exactly as the manifest holds it (may be empty).</param>
        /// <param name="tags">Comma-separated tags (may be empty).</param>
        /// <param name="enabled">Whether the mod is on (loaded, or set to start).</param>
        /// <param name="loadOrder">The manifest load order; 0 or less means none recorded.</param>
        /// <param name="payload">Host data handed back unchanged (may be null).</param>
        public ModFolderModEntry(string id, string name, string folder, string tags, bool enabled,
            long loadOrder = 0, object payload = null)
        {
            Id = (id ?? "").Trim();
            Name = string.IsNullOrWhiteSpace(name) ? Id : name.Trim();
            Folder = folder ?? "";
            Tags = tags ?? "";
            Enabled = enabled;
            LoadOrder = loadOrder;
            Payload = payload;
        }

        /// <summary>Stable mod id.</summary>
        public string Id { get; }

        /// <summary>Display name (the id when the mod has no name).</summary>
        public string Name { get; }

        /// <summary>The stored category text, unnormalized.</summary>
        public string Folder { get; }

        /// <summary>Comma-separated tags.</summary>
        public string Tags { get; }

        /// <summary>Whether the mod is on.</summary>
        public bool Enabled { get; }

        /// <summary>The manifest load order; 0 or less means none recorded.</summary>
        public long LoadOrder { get; }

        /// <summary>Host data passed through unchanged.</summary>
        public object Payload { get; }

        /// <summary>The stored folder read leniently.</summary>
        public ModFolderPath FolderPath => ModFolderPath.FromStored(Folder);
    }
}
