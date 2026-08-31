using System;

namespace CoreAI.Ai
{
    /// <summary>Snapshot of a loaded mod for diagnostics/UI. VM-agnostic (shared by both mod runtimes).</summary>
    public sealed class LuaModInfo
    {
        public string Id = "";
        public LuaCapabilities Capabilities;
        public int HandlerCount;
        public int TimerCount;
        public int ErrorCount;
        public bool LogReports;
        public DateTime LoadedAtUtc;

        /// <summary>Durable actor identity that owns this mod; empty denotes host/system ownership.</summary>
        public string OwnerActorId = "";

        /// <summary>
        /// True when the mod hit its consecutive-error threshold and was quarantined: it stays loaded
        /// and addressable (list/get_source/diagnostics/reload all work) but its handlers, timers, and
        /// queued events are suspended until a reload replaces it. Runtimes without a quarantine
        /// concept always report false.
        /// </summary>
        public bool Quarantined;
    }

    /// <summary>
    /// A single Tick-time mod-handler failure captured for later inspection by the agent (via
    /// <see cref="ILuaModRuntime.GetRecentHandlerErrors"/> and the <c>manage_mods diagnostics</c> action).
    /// Unlike a load/reload error — which propagates synchronously to whoever triggered it — these happen
    /// asynchronously on the host thread, so they are buffered so the agent learns of them on a later turn
    /// and can repair the mod. VM-agnostic (shared by both mod runtimes).
    /// </summary>
    public sealed class LuaModHandlerError
    {
        public string ModId = "";
        public string Error = "";

        /// <summary>Durable owner captured when the error occurred; empty denotes host/system.</summary>
        public string OwnerActorId = "";

        /// <summary>The mod's consecutive-failure streak when this error fired (resets after any success).</summary>
        public int ConsecutiveCount;

        public DateTime AtUtc;
    }
}
