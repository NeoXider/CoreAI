using System.Collections.Generic;

namespace CoreAI.Infrastructure.Luau
{
    /// <summary>Immutable limits and host integration for one synchronous downlevel pass.</summary>
    public sealed class LuauDownlevelOptions
    {
        public LuauDownlevelOptions(int maxInputCharacters = 1048576, int maxOutputCharacters = 4194304,
            int maxEdits = 262144, long maxWorkUnits = 16777216, string trustedIteratorResolverName = null)
        {
            MaxInputCharacters = maxInputCharacters;
            MaxOutputCharacters = maxOutputCharacters;
            MaxEdits = maxEdits;
            MaxWorkUnits = maxWorkUnits;
            TrustedIteratorResolverName = trustedIteratorResolverName;
        }

        /// <summary>Maximum input UTF-16 characters, checked before lexing; must be positive.</summary>
        public int MaxInputCharacters { get; }

        /// <summary>Maximum output or temporary generated-text UTF-16 characters; must be positive.</summary>
        public int MaxOutputCharacters { get; }

        /// <summary>Maximum recorded edit operations, including rolled-back operations; must be positive.</summary>
        public int MaxEdits { get; }

        /// <summary>Maximum charged source/token characters, parser advances and edit-processing units.</summary>
        public long MaxWorkUnits { get; }

        /// <summary>
        /// Optional simple Lua identifier outside the reserved __luau_t prefix, naming a trusted host function
        /// that normalizes an iterator
        /// expression list into iterator/state/control values. It must preserve ordinary iterator
        /// triples and honor actual __iter metamethods without exposing protected metatables.
        /// Null uses the standalone public-getmetatable adapter, which cannot resolve protected metatables.
        /// </summary>
        public string TrustedIteratorResolverName { get; }
    }

    internal sealed class DownlevelBudget
    {
        private readonly LuauDownlevelOptions _options;
        private long _remainingWork;
        private long _outputLength;
        private int _edits;

        public DownlevelBudget(string source, LuauDownlevelOptions options)
        {
            _options = options;
            if (options.MaxInputCharacters <= 0 || options.MaxOutputCharacters <= 0 || options.MaxEdits <= 0
                || options.MaxWorkUnits <= 0)
            {
                throw new LuauDownlevelException("downlevel limits must be positive", 1, 1);
            }

            if (source.Length > options.MaxInputCharacters)
            {
                throw new LuauDownlevelException("downlevel input-character budget exceeded", 1, 1);
            }

            _remainingWork = options.MaxWorkUnits;
            _outputLength = source.Length;
            CheckTextLength(_outputLength);
            Spend(source.Length);
            if (options.TrustedIteratorResolverName != null)
            {
                CheckTextLength(options.TrustedIteratorResolverName.Length + 32L);
                Spend(options.TrustedIteratorResolverName.Length);
            }

            if (!IsResolverName(options.TrustedIteratorResolverName))
            {
                throw new LuauDownlevelException("trusted iterator resolver must be an unreserved simple Lua identifier",
                    1, 1);
            }
        }

        public string TrustedIteratorResolverName => _options.TrustedIteratorResolverName;

        public void Spend(long units, int line = 1, int column = 1)
        {
            if (units > _remainingWork)
            {
                throw new LuauDownlevelException("downlevel work budget exceeded", line, column);
            }

            _remainingWork -= units;
        }

        public void CheckTextLength(long length, int line = 1, int column = 1)
        {
            if (length > _options.MaxOutputCharacters)
            {
                throw new LuauDownlevelException("downlevel output-character budget exceeded", line, column);
            }
        }

        public void RecordEdit(int removed, int added)
        {
            if (_edits >= _options.MaxEdits)
            {
                throw new LuauDownlevelException("downlevel edit budget exceeded", 1, 1);
            }

            CheckTextLength(_outputLength + added - removed);
            Spend(1L + added);
            _edits++;
            _outputLength += added - removed;
        }

        public void RollbackEdit(SourceEdit edit)
        {
            _outputLength -= edit.Text.Length - (edit.End - edit.Start);
        }

        private static bool IsResolverName(string name)
        {
            if (name == null)
            {
                return true;
            }

            if (name.Length == 0 || !IsIdentifierLetter(name[0]))
            {
                return false;
            }

            if (name.StartsWith("__luau_t", System.StringComparison.Ordinal))
            {
                return false;
            }

            for (int index = 1; index < name.Length; index++)
            {
                char character = name[index];
                if (!IsIdentifierLetter(character) && (character < '0' || character > '9'))
                {
                    return false;
                }
            }

            switch (name)
            {
                case "and": case "break": case "do": case "else": case "elseif": case "end":
                case "false": case "for": case "function": case "goto": case "if": case "in":
                case "local": case "nil": case "not": case "or": case "repeat": case "return":
                case "then": case "true": case "until": case "while":
                    return false;
                default:
                    return true;
            }
        }

        private static bool IsIdentifierLetter(char character)
        {
            return character == '_' || character >= 'a' && character <= 'z'
                || character >= 'A' && character <= 'Z';
        }
    }

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

        /// <summary>True when preprocessing emitted source edits, including generic-for normalization.</summary>
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
