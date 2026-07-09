using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// A single <c>report()</c>/<c>print()</c> emission captured for later inspection (via
    /// <see cref="LuaCs.LuaCsModRuntime.GetRecentReports"/>), independent of whether the mod's report
    /// logging flag was enabled at the time — that flag only gates the live
    /// <see cref="LuaCs.LuaCsModRuntime.ModReportEmitted"/> event/log spam, not this bounded history
    /// buffer, so a Hub logs view can still show what a muted mod said. VM-agnostic shape, mirrors
    /// <see cref="LuaModHandlerError"/>.
    /// </summary>
    public sealed class LuaModReport
    {
        public string ModId = "";
        public string Message = "";
        public DateTime AtUtc;
    }
}
