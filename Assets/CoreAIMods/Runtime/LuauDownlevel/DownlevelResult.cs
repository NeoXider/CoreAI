using System.Collections.Generic;

namespace CoreAI.Infrastructure.Luau
{
    /// <summary>Severity of a <see cref="DownlevelDiagnostic"/> produced by <see cref="LuauDownleveler"/>.</summary>
    public enum DownlevelSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    /// <summary>
    /// One machine-readable note about a downlevel pass: what was rewritten, what could change behavior,
    /// or why the source was passed through unchanged. Positions are 1-based line/column in the ORIGINAL
    /// Luau source so an AI self-repair loop can point back at the author's text.
    /// </summary>
    public readonly struct DownlevelDiagnostic
    {
        public DownlevelDiagnostic(DownlevelSeverity severity, string message, int line, int column)
        {
            Severity = severity;
            Message = message ?? "";
            Line = line;
            Column = column;
        }

        public DownlevelSeverity Severity { get; }
        public string Message { get; }
        public int Line { get; }
        public int Column { get; }

        public override string ToString()
        {
            return $"{Severity} ({Line},{Column}): {Message}";
        }
    }

    /// <summary>
    /// Outcome of <see cref="LuauDownleveler.Process(string)"/>. On any parse failure the original
    /// source is returned verbatim with an Error diagnostic — the downleveler never throws and never
    /// emits partially rewritten code.
    /// </summary>
    public sealed class DownlevelResult
    {
        private static readonly IReadOnlyList<DownlevelDiagnostic> Empty = new DownlevelDiagnostic[0];

        public DownlevelResult(string luaSource, bool changed, IReadOnlyList<DownlevelDiagnostic> diagnostics)
        {
            LuaSource = luaSource ?? "";
            Changed = changed;
            Diagnostics = diagnostics ?? Empty;
        }

        /// <summary>Lua 5.2 source when <see cref="Changed"/>; otherwise the original input.</summary>
        public string LuaSource { get; }

        /// <summary>True when at least one Luau construct was rewritten.</summary>
        public bool Changed { get; }

        public IReadOnlyList<DownlevelDiagnostic> Diagnostics { get; }

        public bool HasErrors
        {
            get
            {
                for (int i = 0; i < Diagnostics.Count; i++)
                {
                    if (Diagnostics[i].Severity == DownlevelSeverity.Error)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
