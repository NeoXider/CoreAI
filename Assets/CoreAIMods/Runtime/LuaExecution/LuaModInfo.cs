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

        /// <summary>The mod's consecutive-failure streak when this error fired (resets after any success).</summary>
        public int ConsecutiveCount;

        public DateTime AtUtc;
    }
}