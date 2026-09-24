using System;
using System.Collections.Generic;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// A merged, VM-agnostic view of a single mod for the Hub Mods page: the persisted package
    /// metadata (from <see cref="ILuaModSourceStore"/> and the mod's <c>@coreai</c> header) plus its
    /// live runtime status (loaded? how many handlers/timers/errors) from the active mod runtime.
    /// The page never touches a VM type directly — it renders these records, so it works the same
    /// whether the host wired the MoonSharp <c>LuaModRuntime</c> or the Lua-CSharp runtime.
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

        /// <summary>
        /// Set when a newer bundled version exists but the local copy was user-edited, so it was not
        /// auto-updated on seed. Drives the "Update available" badge in the Mods page.
        /// </summary>
        public bool UpdateAvailable;

        /// <summary>Last bundled version seeded into this entry (empty for user-authored mods).</summary>
        public string SeededVersion = "";

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

        /// <summary>
        /// The runtime quarantined the mod after repeated budget trips and suspended its stored package
        /// (<see cref="LuaModManifest.SuspendedAfterBudgetTrips"/>): it does not start with the game until
        /// it is started or saved by hand.
        /// </summary>
        public bool SuspendedAfterBudgetTrips;
    }

    /// <summary>What a Hub "Save &amp; run" did: a first load, or a reload and what it did with the previous run's objects.</summary>
    public sealed class HubModSaveResult
    {
        public HubModSaveResult(string modId, bool reloaded, ModReloadMode mode, ModReloadReport reload)
        {
            ModId = modId ?? "";
            Reloaded = reloaded;
            Mode = mode;
            Reload = reload;
        }

        /// <summary>The saved mod.</summary>
        public string ModId { get; }

        /// <summary>True when the mod was already loaded and was reloaded; false for a first load.</summary>
        public bool Reloaded { get; }

        /// <summary>The reload mode requested for the save.</summary>
        public ModReloadMode Mode { get; }

        /// <summary>What the reload did with the previous run's startup objects; null for a first load or a runtime that reports nothing.</summary>
        public ModReloadReport Reload { get; }

        /// <summary>The status line the mod editor shows, e.g. "Saved &amp; ran 'castle'; cleaned 126 objects of the previous run."</summary>
        public string Describe()
        {
            string saved = "Saved & ran '" + ModId + "'";
            return Reload == null ? saved + "." : saved + "; " + Reload.Describe() + ".";
        }
    }

    /// <summary>
    /// Starter sources the Mods page "Add" button offers. With the Roblox API wired, the template runs
    /// per-frame work on <c>RunService.Heartbeat</c> and periodic work in a <c>task.spawn</c> loop that
    /// yields with <c>task.wait</c>, each resume under the scheduler's short per-resume budget; only a
    /// composition without the Roblox API gets the legacy <c>hooks_every</c> timer, whose calls run
    /// under the much longer handler budget.
    /// </summary>
    public static class HubModTemplates
    {
        /// <summary>Template for a composition without the Roblox API (legacy hooks).</summary>
        public const string Legacy =
            "--[[@coreai\n" +
            "id: new_mod\n" +
            "name: New Mod\n" +
            "version: 1.0.0\n" +
            "active: true\n" +
            "capabilities: All\n" +
            "author: \n" +
            "description: A new CoreAI Lua mod.\n" +
            "]]\n" +
            "\n" +
            "-- Registered once on load; the host then drives your hooks.\n" +
            "hooks_every(1.0, function()\n" +
            "  report(\"new_mod tick\")\n" +
            "end)\n";

        /// <summary>Template for a composition with the Roblox API (RunService and task).</summary>
        public const string RbxApi =
            "--[[@coreai\n" +
            "id: new_mod\n" +
            "name: New Mod\n" +
            "version: 1.0.0\n" +
            "active: true\n" +
            "capabilities: All\n" +
            "author: \n" +
            "description: A new CoreAI Lua mod.\n" +
            "]]\n" +
            "\n" +
            "-- The main chunk runs once per Save & run. Objects it builds are this run's startup objects:\n" +
            "-- the next Save & run removes them before it runs, unless \"Keep objects\" is on.\n" +
            "local RunService = game:GetService(\"RunService\")\n" +
            "\n" +
            "-- Per-frame work: keep each call short.\n" +
            "local elapsed = 0\n" +
            "RunService.Heartbeat:Connect(function(deltaTime)\n" +
            "  elapsed = elapsed + deltaTime\n" +
            "end)\n" +
            "\n" +
            "-- Periodic work: a loop that yields with task.wait between steps.\n" +
            "task.spawn(function()\n" +
            "  while true do\n" +
            "    print(\"new_mod tick\")\n" +
            "    task.wait(1)\n" +
            "  end\n" +
            "end)\n";

        /// <summary>The template for a composition with or without the Roblox API.</summary>
        public static string For(bool rbxApiAvailable)
        {
            return rbxApiAvailable ? RbxApi : Legacy;
        }
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
    /// adapter over a concrete mod runtime (<c>LuaModRuntime</c> or the Lua-CSharp runtime)
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

        /// <summary>
        /// <see cref="SaveOrReload(string, string)"/> with an explicit reload <paramref name="mode"/> for an
        /// already loaded mod (the mode-less overload cleans the previous run's startup objects), returning
        /// what the save did. Throws like the mode-less overload.
        /// </summary>
        HubModSaveResult SaveOrReload(string id, string code, ModReloadMode mode);

        /// <summary>The starter source the Mods page "Add" button opens, matched to the composition (<see cref="HubModTemplates"/>).</summary>
        string NewModTemplate { get; }

        /// <summary>Loads a dormant stored mod (using its persisted source) and marks it active.</summary>
        void Enable(string id);

        /// <summary>Unloads a loaded mod and marks its stored package dormant (source kept).</summary>
        void Disable(string id);

        /// <summary>Unloads (if loaded) and permanently deletes the stored package. Returns true.</summary>
        bool Delete(string id);

        /// <summary>
        /// Revision history recorded for a mod (empty when the runtime tracks no history for it, e.g. it
        /// was never loaded through a version-tracked runtime).
        /// </summary>
        IReadOnlyList<LuaScriptRevision> ListModVersions(string id);

        /// <summary>
        /// Reverts a mod to a recorded revision, returning its source. False when the mod has no such
        /// revision. Throws if the restored source fails to reload (mirrors <see cref="SaveOrReload"/>).
        /// </summary>
        bool TryRevertMod(string id, int revisionIndex, out string restoredSource);

        /// <summary>Serializes a mod (loaded or stored) to a shareable JSON bundle. Null when the id is unknown.</summary>
        string ExportMod(string id);

        /// <summary>
        /// Installs a mod from an <see cref="ExportMod"/> bundle, capped by this service's host
        /// capability grant (and, unless the service was built with <c>allowFull</c>, stripped of
        /// <see cref="LuaCapabilities.Full"/>). Returns false on malformed JSON or a missing id/source.
        /// </summary>
        bool ImportMod(string bundleJson);

        /// <summary>
        /// Re-seeds a mod from its bundled source (matched by id via the registered bundled-mod source),
        /// clearing <see cref="HubModRecord.UpdateAvailable"/>. Returns false when the mod has no bundled
        /// counterpart, or the stored entry is user-authored (no bundled origin) rather than a
        /// previously-seeded package.
        /// </summary>
        bool ApplyBundledUpdate(string id);

        /// <summary>Human-readable recent runtime handler/timer errors for a mod (empty when none).</summary>
        string RecentErrors(string id);

        /// <summary>
        /// Structured recent Tick-time handler/timer errors (all mods when <paramref name="modId"/> is
        /// null), oldest first. Same underlying data as <see cref="RecentErrors"/>, kept as objects so a
        /// UI can merge/sort them against <see cref="RecentReports"/> by timestamp.
        /// </summary>
        IReadOnlyList<LuaModHandlerError> RecentErrorEntries(string modId = null);

        /// <summary>
        /// Recent <c>report()</c>/<c>print()</c> emissions (all mods when <paramref name="modId"/> is
        /// null), oldest first, independent of each mod's report-logging mute flag. Empty when the
        /// underlying runtime keeps no report history.
        /// </summary>
        IReadOnlyList<LuaModReport> RecentReports(string modId = null);

        /// <summary>Clears the recent reports buffer (all mods). No-op where the runtime keeps no report history.</summary>
        void ClearReports();

        /// <summary>Clears the recent handler-errors buffer (all mods).</summary>
        void ClearErrors();

        /// <summary>Whether a mod's <c>report()</c>/<c>print()</c> output is surfaced live (muted by default).</summary>
        bool GetReportLoggingEnabled(string modId);

        /// <summary>Enables/disables a mod's live report output. Returns false if the mod is unknown.</summary>
        bool SetReportLoggingEnabled(string modId, bool enabled);

        /// <summary>Raised after any mod is loaded, reloaded, unloaded, or deleted (including by the AI tool).</summary>
        event Action ModsChanged;

        /// <summary>Raised after a handler error or report is recorded, for live-refreshing log views.</summary>
        event Action LogsChanged;
    }
}
