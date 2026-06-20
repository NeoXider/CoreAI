#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
namespace CoreAI.Ai
{
    /// <summary>
    /// Portable, JSON-serializable description of a persisted Lua mod package. This is the metadata
    /// half of a shareable mod (the other half being its Lua source): it travels with the source in
    /// an <see cref="ILuaModSourceStore"/> entry and inside an export/import bundle, so a mod can
    /// survive a restart and be moved between hosts.
    /// <para>
    /// <see cref="Capabilities"/> is the granted <see cref="LuaCapabilities"/> flag set rendered as a
    /// string (round-trips via <c>Enum.Parse</c>) rather than the enum itself, so the manifest stays a
    /// plain data contract that serializes identically regardless of the enum's underlying numeric
    /// layout. The persisted capability set is only ever a <em>request</em>: on rehydrate/import it is
    /// intersected with the host grant and (unless explicitly allowed) stripped of
    /// <see cref="LuaCapabilities.Full"/>, so a shared mod can never silently escalate.
    /// </para>
    /// </summary>
    public sealed class LuaModManifest
    {
        /// <summary>Stable mod identifier (also the storage key).</summary>
        public string Id = "";

        /// <summary>Human-readable display name.</summary>
        public string Name = "";

        /// <summary>Optional free-text description of what the mod does.</summary>
        public string Description = "";

        /// <summary>Optional version string (host-defined format, e.g. semantic version).</summary>
        public string Version = "";

        /// <summary>Optional author/attribution.</summary>
        public string Author = "";

        /// <summary>
        /// The granted <see cref="LuaCapabilities"/> flag set as a string (round-trips via
        /// <c>Enum.Parse</c>). Treated as a request only; never trusted to escalate on load.
        /// </summary>
        public string Capabilities = "";

        /// <summary>
        /// Whether the mod should auto-load on rehydrate. Unloading marks this false (the package is
        /// kept but dormant); deleting removes the package entirely.
        /// </summary>
        public bool Active = true;

        /// <summary>Entry-point file name within the package; defaults to <c>main.lua</c>.</summary>
        public string Entry = "main.lua";
    }
}
#endif
