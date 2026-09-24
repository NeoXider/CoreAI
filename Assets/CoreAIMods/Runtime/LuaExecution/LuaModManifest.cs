using System.Collections.Generic;
using Newtonsoft.Json;

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

        /// <summary>
        /// Optional "/"-separated category path used for tree grouping in mod UI.
        /// </summary>
        public string Category = "";

        /// <summary>Comma-separated tags for filtering and organization.</summary>
        public string Tags = "";

        /// <summary>
        /// Origin/source marker for bundled mods: empty for user-authored entries,
        /// otherwise identifiers like <c>resources</c>, <c>streamingassets</c>,
        /// <c>addressables:&lt;label&gt;</c>, or <c>remote:&lt;url&gt;</c>.
        /// </summary>
        public string Origin = "";

        /// <summary>Last bundled version seeded into this store entry.</summary>
        public string SeededVersion = "";

        /// <summary>Source hash recorded at seed time to detect user edits.</summary>
        public string SeededHash = "";

        /// <summary>Optional author/attribution.</summary>
        public string Author = "";

        /// <summary>Durable actor identity that owns this mod; empty denotes host/system ownership.</summary>
        public string OwnerActorId = "";

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

        /// <summary>
        /// Set when a newer bundled version exists but the local file was user-edited;
        /// can be used to surface a manual update action in UI.
        /// </summary>
        public bool UpdateAvailable = false;

        /// <summary>Entry-point file name within the package; defaults to <c>main.lua</c>.</summary>
        public string Entry = "main.lua";

        /// <summary>
        /// Where the mod sits in its world's load order, so a restart or a world restore starts mods in
        /// the order they were loaded and a mod may use what an earlier one made at init. Stamped with
        /// <see cref="NextLoadOrder"/> when a mod is first loaded (or loaded again after an unload), kept
        /// by every rewrite of an existing mod, never changed by a rehydrate. <c>0</c> (or any value
        /// below 1) means no recorded order: a package written before the field existed, a bundled mod
        /// seeded on install, or a mod first loaded while its store could not be listed. Such mods start
        /// first, by ordinal id, because an active one had already started at startup before any mod with
        /// a recorded order was first loaded. A value above <see cref="MaximumLoadOrder"/> is no recorded
        /// order either (<see cref="IsRecordedLoadOrder"/>), and a world package that carries one is refused.
        /// </summary>
        // WHY omitted when 0 and no format_version bump: an unordered manifest stays byte-identical to
        // the one written before this field existed, so a store or world package without load order
        // reads and writes exactly as before. A reader that predates the field refuses a world package
        // whose mods carry a load order, explicitly (strict unknown-member check: "Could not find
        // member 'LoadOrder'"); it never restores such a package in the wrong order.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public long LoadOrder;

        /// <summary>
        /// Set, together with <see cref="Active"/> = false, when the runtime quarantined the mod after
        /// repeated budget trips (instruction, time or memory budget), so neither a restart nor a world
        /// restore starts it again; the Hub shows it as suspended until it is started by hand. A
        /// successful load or reload writes a fresh manifest without it.
        /// </summary>
        // WHY omitted when false, like LoadOrder: a manifest of a mod that was never suspended stays
        // byte-identical to one written before this field existed.
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool SuspendedAfterBudgetTrips;

        /// <summary>
        /// The largest <see cref="LoadOrder"/> a store records: 2^53. The order counts first loads, so no
        /// store reaches it (one first load per microsecond would take 285 years), and every JSON reader
        /// keeps a value up to it exact.
        /// </summary>
        public const long MaximumLoadOrder = 9007199254740992L;

        /// <summary>
        /// True when <paramref name="loadOrder"/> is a recorded order: 1 up to <see cref="MaximumLoadOrder"/>.
        /// Anything else reads as no recorded order.
        /// </summary>
        public static bool IsRecordedLoadOrder(long loadOrder)
        {
            return loadOrder > 0 && loadOrder <= MaximumLoadOrder;
        }

        /// <summary>
        /// The <see cref="LoadOrder"/> for a mod that is being loaded for the first time into
        /// <paramref name="store"/>: one past the highest recorded value stored there, dormant packages
        /// included, so the value keeps growing across restarts and never reuses a dormant mod's place.
        /// The one rule every writer of a first-time manifest uses (the runtime, and a Hub save the
        /// runtime did not already persist). A <see cref="ILuaModSourceStore.List"/> that fails, by
        /// throwing or by answering a listing its store marked unreadable, throws; callers treat it as
        /// no recorded order and store the mod without one.
        /// </summary>
        /// <remarks>
        /// WHY a stored value above <see cref="MaximumLoadOrder"/> is skipped rather than followed: only a
        /// hand-edited store holds one (a world package that carries one is refused on read), and following
        /// it saturated every later first load at long.MaxValue, where they all tied and fell back to id
        /// order for the rest of the world's life. Skipped, it reads as unordered like
        /// <see cref="IsRecordedLoadOrder"/> says, and later first loads keep their order.
        /// </remarks>
        // WHY public, not internal: the Hub service that writes manifests lives in its own assembly
        // (CoreAI.Mods.Hub), and any host tool that writes a manifest into the store needs the same rule.
        public static long NextLoadOrder(ILuaModSourceStore store)
        {
            IReadOnlyList<LuaModManifest> stored = store?.List();
            if (stored is UnreadableLuaModSourceListing unreadable)
            {
                throw new System.IO.IOException(unreadable.Reason);
            }

            long highest = 0;
            if (stored != null)
            {
                foreach (LuaModManifest manifest in stored)
                {
                    if (manifest != null && IsRecordedLoadOrder(manifest.LoadOrder) && manifest.LoadOrder > highest)
                    {
                        highest = manifest.LoadOrder;
                    }
                }
            }

            return highest < MaximumLoadOrder ? highest + 1 : MaximumLoadOrder;
        }
    }

    /// <summary>
    /// The empty answer a source store gives <see cref="ILuaModSourceStore.List"/> when it could not read
    /// its packages at all: every caller that only shows or restores mods sees an empty list, as before,
    /// while <see cref="LuaModManifest.NextLoadOrder"/> can tell "unreadable" from "empty" (and wrappers
    /// that forward <see cref="ILuaModSourceStore.List"/> pass it on unchanged).
    /// </summary>
    internal sealed class UnreadableLuaModSourceListing : IReadOnlyList<LuaModManifest>
    {
        public UnreadableLuaModSourceListing(string reason)
        {
            Reason = string.IsNullOrWhiteSpace(reason)
                ? "The mod source store could not list its mods."
                : reason;
        }

        /// <summary>Why the store could not list its mods.</summary>
        public string Reason { get; }

        public int Count => 0;

        public LuaModManifest this[int index] => throw new System.ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<LuaModManifest> GetEnumerator()
        {
            yield break;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
