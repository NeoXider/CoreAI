using System;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Thrown by <see cref="LuauSourceGate"/> when incoming source carries a downlevel Error the
    /// rewriter could not lower to Lua 5.2. Carries the aggregated downlevel diagnostics so the mod
    /// load / <c>execute_lua</c> / envelope path surfaces the real syntax problem instead of an opaque
    /// VM parse failure on the raw source.
    /// </summary>
    public sealed class LuauDownlevelSyntaxException : Exception
    {
        public LuauDownlevelSyntaxException(string message) : base(message)
        {
        }
    }
}
