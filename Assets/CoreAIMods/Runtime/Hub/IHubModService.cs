using System;
using System.Collections.Generic;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// A merged, VM-agnostic view of a single mod for the Hub Mods page: the persisted package
    /// metadata (from <see cref="ILuaModSourceStore"/> and the mod's <c>@coreai</c> header) plus its
    /// live runtime status (loaded? how many handlers/timers/errors) from the active mod runtime.
    /// The page never touches a VM type directly — it renders these records, so it works the same
    /// whether the host wired the MoonSharp <see cref="LuaModRuntime"/> or the Lua-CSharp runtime.
    /// </summary>
    public sealed class HubModRecord
    {
        /// <summary>Stable mod id (storage key and runtime identity).</summary>
        public string Id = "";

        /// <summary>Display name (header <c>name:</c>, falls back to the id).</summary>
        public string Name = "";

        /// <summary>"/"-separated category path used for the grouping tree (may be empty).</summary>
        public string Category = "";

        /// <summary>Comma-separated tags used by search (may be empty).</summary>
        public string Tags = "";

        /// <summary>One-line description from the header (may be empty).</summary>
        public string Description = "";

        /// <summary>Author/attribution from the header (may be empty).</summary>
        public string Author = "";

        /// <summary>Version string from the header (may be empty).</summary>
        public string Version = "";

        /// <summary>Origin marker for bundled mods (resources/streamingassets/... or empty for user mods).</summary>
        public string Origin = "";

        /// <summary>Granted capability tier rendered as a string.</summary>
        public string Capabilities = "";

        /// <summary>True when the mod is currently loaded in the runtime.</summary>
        public bool IsLoaded;

        /// <summary>The persisted <see cref="LuaModManifest.Active"/> flag (auto-load intent).</summary>
        public bool StoredActive;

        /// <summary>True when the mod exists only in the store (not currently loaded).</summary>
        public bool IsStored;

        /// <summary>Registered event handlers (live count; 0 when not loaded).</summary>
        public int Handlers;

        /// <summary>Registered timers (live count; 0 when not loaded).</summary>
        public int Timers;

        /// <summary>Consecutive runtime error count (live; 0 when not loaded).</summary>
        public int Errors;
    }

    /// <summary>Minimal live-status projection each runtime adapter maps its own info type onto.</summary>
    public readonly struct HubLoadedInfo
    {
        public HubLoadedInfo(string id, LuaCapabilities caps, int handlers, int timers, int errors)
        {
            Id = id ?? "";
            Capabilities = caps;
            Handlers = handlers;
            Timers = timers;
            Errors = errors;
        }

        public string Id { get; }
        public LuaCapabilities Capabilities { get; }
        public int Handlers { get; }
        public int Timers { get; }
        public int Errors { get; }
    }

    /// <summary>
    /// VM-agnostic CRUD/query surface the Hub Mods page and editor drive. Implemented by a thin
    /// adapter over a concrete mod runtime (<see cref="LuaModRuntime"/> or the Lua-CSharp runtime)
    /// plus the shared <see cref="ILuaModSourceStore"/>. Every mutating call routes through the real
    /// runtime + store (load/reload/unload/delete/persist) — nothing is stubbed.
    /// </summary>
    public interface IHubModService
    {
        /// <summary>True when the Lua sandbox is available on this platform.</summary>
        bool IsSupported { get; }

        /// <summary>
        /// Merged snapshot of every known mod: stored packages (active + dormant) unioned with the
        /// live loaded set, enriched with each mod's <c>@coreai</c> header fields for grouping/search.
        /// </summary>
        IReadOnlyList<HubModRecord> ListMods();

        /// <summary>Returns a mod's Lua source (live copy if loaded, else the stored package). False when neither holds it.</summary>
        bool TryGetSource(string id, out string source);

        /// <summary>True when a mod with this id is currently loaded.</summary>
        bool IsLoaded(string id);

        /// <summary>
        /// Persists and (re)runs a mod: reloads it when already loaded, otherwise loads it fresh, then
        /// writes source + a header-derived manifest to the store. Throws on a Lua compile/run error so
        /// the editor can surface it (this doubles as the validate affordance).
        /// </summary>
        void SaveOrReload(string id, string code);

        /// <summary>Loads a dormant stored mod (using its persisted source) and marks it active.</summary>
        void Enable(string id);

        /// <summary>Unloads a loaded mod and marks its stored package dormant (source kept).</summary>
        void Disable(string id);

        /// <summary>Unloads (if loaded) and permanently deletes the stored package. Returns true.</summary>
        bool Delete(string id);

        /// <summary>Human-readable recent runtime handler/timer errors for a mod (empty when none).</summary>
        string RecentErrors(string id);

        /// <summary>Raised after any mod is loaded, reloaded, unloaded, or deleted (including by the AI tool).</summary>
        event Action ModsChanged;
    }
}
