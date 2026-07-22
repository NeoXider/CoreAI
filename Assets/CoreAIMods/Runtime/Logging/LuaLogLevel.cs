namespace CoreAI.Ai.Logging
{
    /// <summary>
    /// Severity of a captured <see cref="LuaLogEntry"/>. Ordered so a numeric comparison
    /// (<c>Level &gt;= minLevel</c>) works as a "this severity or worse" filter in
    /// <see cref="ILuaLogService.Query"/>.
    /// </summary>
    public enum LuaLogLevel
    {
        /// <summary>Lua <c>print()</c> / <c>report()</c> output.</summary>
        Print = 0,

        /// <summary>Lua <c>warn()</c> output.</summary>
        Warn = 1,

        /// <summary>Lua-authored <c>error()</c> call.</summary>
        Error = 2,

        /// <summary>Uncaught VM/host exception thrown while running a mod's Lua code.</summary>
        RuntimeError = 3
    }
}
