using System;

namespace CoreAI.Ai.Logging
{
    /// <summary>
    /// A single captured Lua mod log line (<c>print</c>/<c>warn</c>/<c>error</c> or an uncaught
    /// runtime error), independent of the Unity console. <see cref="Sequence"/> and
    /// <see cref="UtcTime"/> are assigned by <see cref="ILuaLogService.Append"/> — any values already
    /// set on the entry when it is passed in are overwritten, so callers only need to fill in the
    /// remaining fields.
    /// </summary>
    public sealed class LuaLogEntry
    {
        /// <summary>Monotonically increasing id assigned by the log service on append; 0 until then.</summary>
        public long Sequence;

        /// <summary>UTC wall-clock time the entry was appended.</summary>
        public DateTime UtcTime;

        /// <summary>Caller-supplied in-game time (e.g. <c>Time.time</c>) at the moment of the log call.</summary>
        public float GameTime;

        /// <summary>Id of the mod that produced this entry. Never null (empty string for host-level entries).</summary>
        public string ModId = "";

        /// <summary>Severity of the entry.</summary>
        public LuaLogLevel Level;

        /// <summary>The logged message text.</summary>
        public string Message = "";

        /// <summary>Optional name of the Lua script/chunk the entry originated from.</summary>
        public string ScriptName;

        /// <summary>Optional source line number within <see cref="ScriptName"/>.</summary>
        public int? Line;
    }
}
