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

        /// <summary>Replaces a loaded mod's code, keeping its granted capabilities.</summary>
        void ReloadMod(ActorContext caller, string id, string luaCode);

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
