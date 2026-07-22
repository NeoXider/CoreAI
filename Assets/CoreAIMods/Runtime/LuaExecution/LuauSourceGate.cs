using System.Text;
using CoreAI.Infrastructure.Luau;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Single entry every Lua-CSharp runtime path calls to accept Luau syntax: it runs the engine-free
    /// <see cref="LuauDownleveler"/> over incoming source BEFORE the bundled Lua 5.2 VM compiles it, so
    /// Luau-only constructs (compound assignment <c>+=</c>, <c>continue</c>, backtick string
    /// interpolation, if-then-else expressions, type annotations/casts, Luau number literals) parse
    /// instead of failing at load/exec time.
    ///
    /// FAIL LOUD, NEVER FALL BACK: a downlevel Error (malformed input the rewriter could not lower) is
    /// surfaced as the load/exec failure via <see cref="LuauDownlevelSyntaxException"/> instead of
    /// compiling the raw source. Falling back to raw would only re-throw an opaque Lua parse error one
    /// layer down and bury the real diagnostic.
    ///
    /// LINE NUMBERS PRESERVED: the downleveler rewrites via newline-preserving deletions
    /// (<c>DeleteKeepNewlines</c> re-emits the newlines it removes), so a runtime error's line number
    /// still points at the user's ORIGINAL source line. Two documented edge cases can shift later lines
    /// — a multi-line <c>repeat/until</c> whose condition is duplicated to host a <c>continue</c>, and a
    /// <c>\z</c> escape that swallows a real newline — and both are flagged by the downleveler with a
    /// diagnostic when they occur.
    ///
    /// DEFAULT ON, NO FLAG: valid Lua 5.2 is passed through byte-identically (<c>NeedsDownlevel</c>
    /// returns false and the original string instance is returned), so plain-Lua scripts see no
    /// behavior change and no opt-in flag is warranted.
    /// </summary>
    internal static class LuauSourceGate
    {
        /// <summary>
        /// Downlevels <paramref name="source"/> to Lua 5.2, tagging diagnostics with
        /// <paramref name="chunkName"/> (mod id / <c>execute_lua</c> / <c>envelope</c>) so error line
        /// mapping stays meaningful. Throws <see cref="LuauDownlevelSyntaxException"/> when the input
        /// carries a downlevel Error; otherwise returns the compilable source (the original instance
        /// when nothing needed rewriting).
        /// </summary>
        public static string ToLua52(string source, string chunkName)
        {
            string name = string.IsNullOrWhiteSpace(chunkName) ? "chunk" : chunkName;
            DownlevelResult result = LuauDownleveler.Process(source ?? "", name);
            if (result.HasErrors)
            {
                throw new LuauDownlevelSyntaxException(BuildErrorMessage(result));
            }

            return result.LuaSource;
        }

        private static string BuildErrorMessage(DownlevelResult result)
        {
            StringBuilder sb = new();
            sb.Append("Luau syntax error: ");
            bool first = true;
            foreach (DownlevelDiagnostic d in result.Diagnostics)
            {
                if (d.Severity != DownlevelSeverity.Error)
                {
                    continue;
                }

                if (!first)
                {
                    sb.Append("; ");
                }

                sb.Append(d.Message).Append(" (line ").Append(d.Line).Append(')');
                first = false;
            }

            return sb.ToString();
        }
    }
}
