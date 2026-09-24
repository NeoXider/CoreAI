using System;
using System.Collections.Generic;
using CoreAI.Authority;

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
        /// <summary>Snapshot of non-sensitive loaded-mod metadata visible to every trusted caller.</summary>
        IReadOnlyList<LuaModInfo> ListMods(ActorContext caller);

        /// <summary>Returns the last-loaded source for a mod, false if it is not loaded.</summary>
        bool TryGetModSource(ActorContext caller, string id, out string source);

        /// <summary>Loads a caller-owned mod at the given capability tier, optionally persisting it.</summary>
        void LoadMod(ActorContext caller, string id, string luaCode,
            LuaCapabilities capabilities = LuaCapabilities.All,
            bool persistToStore = true);

        /// <summary>Returns the durable owner id, empty for host/system, or null when the mod is unknown.</summary>
        string GetModOwnerActorId(ActorContext caller, string id);

        /// <summary>
        /// Replaces a loaded mod's code, keeping its granted capabilities, in the runtime's default
        /// <see cref="ModReloadMode"/> (<see cref="ModReloadMode.CleanStartupObjects"/> for the
        /// Lua-CSharp runtime).
        /// </summary>
        void ReloadMod(ActorContext caller, string id, string luaCode);

        /// <summary>
        /// Replaces a loaded mod's code in <paramref name="mode"/> and answers what the reload did with
        /// the startup objects of the run it replaced; null when the runtime cannot tell.
        /// </summary>
        /// <remarks>
        /// A wrapper around another <see cref="ILuaModRuntime"/> (a world-session facade, an attribution
        /// facade) must forward this member, or the mode is lost on the way. The default is for a runtime
        /// that tracks no startup objects: it runs its only reload,
        /// <see cref="ReloadMod(ActorContext, string, string)"/>, and answers null.
        /// </remarks>
        ModReloadReport ReloadMod(ActorContext caller, string id, string luaCode, ModReloadMode mode)
        {
            ReloadMod(caller, id, luaCode);
            return null;
        }

        /// <summary>Unloads a mod (persisted state, if any, goes dormant). False if it was not loaded.</summary>
        bool UnloadMod(ActorContext caller, string id);

        /// <summary>Serializes a mod to a shareable bundle string.</summary>
        string ExportMod(ActorContext caller, string id);

        /// <summary>Installs a mod from a bundle, capped by the host grant. Full is stripped unless allowed.</summary>
        bool ImportMod(ActorContext caller, string bundleJson, LuaCapabilities hostGrant,
            bool allowFull = false);

        /// <summary>Unloads a mod and deletes its persisted source/state. False if unknown.</summary>
        bool ForgetMod(ActorContext caller, string id);

        /// <summary>Lists the revision history recorded for a mod (newest first is VM-defined).</summary>
        IReadOnlyList<LuaScriptRevision> ListModVersions(ActorContext caller, string id);

        /// <summary>Reverts a mod to an earlier revision, returning the restored source.</summary>
        bool TryRevertMod(ActorContext caller, string id, int revisionIndex, out string restoredSource);

        /// <summary>Recent asynchronous hook/timer failures (all mods when modId is null).</summary>
        IReadOnlyList<LuaModHandlerError> GetRecentHandlerErrors(ActorContext caller, string modId = null);

        /// <summary>Recent mod report records visible to the trusted caller.</summary>
        IReadOnlyList<LuaModReport> GetRecentReports(ActorContext caller, string modId = null);

        /// <summary>Clears recent handler failures and returns the removed count.</summary>
        int ClearRecentHandlerErrors(ActorContext caller, string modId = null);

        /// <summary>Clears recent report records and returns the removed count.</summary>
        int ClearRecentReports(ActorContext caller, string modId = null);

        /// <summary>Advances mod timers and dispatches queued events; the host calls this once per frame.</summary>
        void Tick(ActorContext caller, double deltaSeconds);

        /// <summary>Emits a named event with a payload to every loaded mod's matching hooks_on handlers.</summary>
        void EmitEvent(ActorContext caller, string name, string payload = "");

        /// <summary>True when a mod with the given id is currently loaded.</summary>
        bool IsLoaded(ActorContext caller, string id);

        /// <summary>Whether a mod's <c>report()</c> calls are surfaced (muted by default to avoid log spam).</summary>
        bool GetModReportLoggingEnabled(ActorContext caller, string id);

        /// <summary>Enables/disables a mod's <c>report()</c> log output. Returns false if the mod is unknown.</summary>
        bool SetModReportLoggingEnabled(ActorContext caller, string id, bool enabled);

        /// <summary>Adds an unrestricted host observer for hook/timer failures.</summary>
        void AddModHandlerErroredListener(ActorContext caller, Action<string, string, int> listener);

        /// <summary>Removes an unrestricted host observer for hook/timer failures.</summary>
        void RemoveModHandlerErroredListener(ActorContext caller, Action<string, string, int> listener);

        /// <summary>Adds an unrestricted host observer for successful source loads and reloads.</summary>
        void AddModSourceLoadedListener(ActorContext caller,
            Action<string, string, LuaCapabilities> listener);

        /// <summary>Removes an unrestricted host observer for successful source loads and reloads.</summary>
        void RemoveModSourceLoadedListener(ActorContext caller,
            Action<string, string, LuaCapabilities> listener);

        /// <summary>Adds an unrestricted host observer for source unloads.</summary>
        void AddModSourceUnloadedListener(ActorContext caller,
            Action<string, string, LuaCapabilities> listener);

        /// <summary>Removes an unrestricted host observer for source unloads.</summary>
        void RemoveModSourceUnloadedListener(ActorContext caller,
            Action<string, string, LuaCapabilities> listener);

        /// <summary>Adds an unrestricted host observer for inter-mod events.</summary>
        void AddModEventEmittedListener(ActorContext caller, Action<string, string, string> listener);

        /// <summary>Removes an unrestricted host observer for inter-mod events.</summary>
        void RemoveModEventEmittedListener(ActorContext caller, Action<string, string, string> listener);

        /// <summary>Adds an unrestricted host observer for enabled report output.</summary>
        void AddModReportEmittedListener(ActorContext caller, Action<string, string> listener);

        /// <summary>Removes an unrestricted host observer for enabled report output.</summary>
        void RemoveModReportEmittedListener(ActorContext caller, Action<string, string> listener);
    }
}
