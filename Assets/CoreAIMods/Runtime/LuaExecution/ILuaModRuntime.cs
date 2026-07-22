using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// VM-agnostic surface of a persistent Lua mod runtime, so the <c>manage_mods</c> tool and the
    /// auto-repair bridge work against either the MoonSharp <c>LuaModRuntime</c> or the Lua-CSharp
    /// <see cref="LuaCsModRuntime"/>. Only the members those consumers need are exposed; VM-specific
    /// construction, ticking, and rehydration stay on the concrete runtime types.
    /// </summary>
    public interface ILuaModRuntime
    {
        /// <summary>Snapshot of all loaded mods.</summary>
        IReadOnlyList<LuaModInfo> ListMods();

        /// <summary>Returns the last-loaded source for a mod, false if it is not loaded.</summary>
        bool TryGetModSource(string id, out string source);

        /// <summary>Loads (or replaces) a mod at the given capability tier, optionally persisting it.</summary>
        void LoadMod(string id, string luaCode, LuaCapabilities capabilities = LuaCapabilities.All,
            bool persistToStore = true);

        /// <summary>Replaces a loaded mod's code, keeping its granted capabilities.</summary>
        void ReloadMod(string id, string luaCode);

        /// <summary>Unloads a mod (persisted state, if any, goes dormant). False if it was not loaded.</summary>
        bool UnloadMod(string id);

        /// <summary>Serializes a mod to a shareable bundle string.</summary>
        string ExportMod(string id);

        /// <summary>Installs a mod from a bundle, capped by the host grant. Full is stripped unless allowed.</summary>
        bool ImportMod(string bundleJson, LuaCapabilities hostGrant, bool allowFull = false);

        /// <summary>Unloads a mod and deletes its persisted source/state. False if unknown.</summary>
        bool ForgetMod(string id);

        /// <summary>Lists the revision history recorded for a mod (newest first is VM-defined).</summary>
        IReadOnlyList<LuaScriptRevision> ListModVersions(string id);

        /// <summary>Reverts a mod to an earlier revision, returning the restored source.</summary>
        bool TryRevertMod(string id, int revisionIndex, out string restoredSource);

        /// <summary>Recent asynchronous hook/timer failures (all mods when modId is null).</summary>
        IReadOnlyList<LuaModHandlerError> GetRecentHandlerErrors(string modId = null);

        /// <summary>Advances mod timers and dispatches queued events; the host calls this once per frame.</summary>
        void Tick(double deltaSeconds);

        /// <summary>Emits a named event with a payload to every loaded mod's matching hooks_on handlers.</summary>
        void EmitEvent(string name, string payload = "");

        /// <summary>True when a mod with the given id is currently loaded.</summary>
        bool IsLoaded(string id);

        /// <summary>Whether a mod's <c>report()</c> calls are surfaced (muted by default to avoid log spam).</summary>
        bool GetModReportLoggingEnabled(string id);

        /// <summary>Enables/disables a mod's <c>report()</c> log output. Returns false if the mod is unknown.</summary>
        bool SetModReportLoggingEnabled(string id, bool enabled);

        /// <summary>(modId, error, consecutiveErrorCount) when a loaded mod's hook/timer throws under Tick.</summary>
        event Action<string, string, int> ModHandlerErrored;

        /// <summary>(modId, source, caps) after a mod source is successfully loaded or reloaded.</summary>
        event Action<string, string, LuaCapabilities> ModSourceLoaded;

        /// <summary>(modId, source, caps) after a mod is explicitly unloaded. Repeated runtime errors quarantine a mod (kept loaded, dispatch suspended) instead of unloading it.</summary>
        event Action<string, string, LuaCapabilities> ModSourceUnloaded;

        /// <summary>(modId, eventName, payload) when a mod calls <c>events_emit</c>.</summary>
        event Action<string, string, string> ModEventEmitted;

        /// <summary>(modId, message) when a mod calls <c>report</c>/<c>print</c> and its report logging is on.</summary>
        event Action<string, string> ModReportEmitted;
    }
}
