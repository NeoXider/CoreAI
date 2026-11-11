using System;

namespace CoreAI.RuntimeUi
{
    /// <summary>How serious a runtime UI report is.</summary>
    public enum RuntimeUiDiagnosticSeverity
    {
        /// <summary>Accepted, with a note the author may want to read (a CoreAI extension was used).</summary>
        Info = 0,

        /// <summary>Something was not applied (unsupported rule, property, attribute or element).</summary>
        Warning = 1,

        /// <summary>The source is broken at this point; the interpreter recovered and continued.</summary>
        Error = 2
    }

    /// <summary>Which text or call a runtime UI report points into.</summary>
    public enum RuntimeUiSource
    {
        /// <summary>UXML markup.</summary>
        Uxml = 0,

        /// <summary>A USS style sheet.</summary>
        Uss = 1,

        /// <summary>A <c>style</c> attribute in UXML or a single property set through the API.</summary>
        InlineStyle = 2,

        /// <summary>A call on the runtime UI host or one of its screens.</summary>
        Api = 3
    }

    /// <summary>
    /// One report from the UXML parser, the USS parser, the cascade or the host. Every rule, property,
    /// attribute or element the interpreter does not apply produces one of these with its line, so an
    /// author (or the AI) can correct the source instead of guessing why nothing changed.
    /// </summary>
    public readonly struct RuntimeUiDiagnostic : IEquatable<RuntimeUiDiagnostic>
    {
        /// <summary>Creates a report.</summary>
        /// <param name="code">Stable code from <see cref="RuntimeUiDiagnosticCodes"/>.</param>
        /// <param name="severity">Severity.</param>
        /// <param name="source">Which text the line refers to.</param>
        /// <param name="line">1-based line, or 0 when the report has no position.</param>
        /// <param name="column">1-based column, or 0 when the report has no position.</param>
        /// <param name="message">Human-readable explanation.</param>
        public RuntimeUiDiagnostic(
            string code,
            RuntimeUiDiagnosticSeverity severity,
            RuntimeUiSource source,
            int line,
            int column,
            string message)
        {
            Code = code ?? string.Empty;
            Severity = severity;
            Source = source;
            Line = line;
            Column = column;
            Message = message ?? string.Empty;
        }

        /// <summary>Stable code from <see cref="RuntimeUiDiagnosticCodes"/>.</summary>
        public string Code { get; }

        /// <summary>Severity.</summary>
        public RuntimeUiDiagnosticSeverity Severity { get; }

        /// <summary>Which text the line refers to.</summary>
        public RuntimeUiSource Source { get; }

        /// <summary>1-based line, or 0 when the report has no position.</summary>
        public int Line { get; }

        /// <summary>1-based column, or 0 when the report has no position.</summary>
        public int Column { get; }

        /// <summary>Human-readable explanation.</summary>
        public string Message { get; }

        /// <inheritdoc />
        public bool Equals(RuntimeUiDiagnostic other)
        {
            return string.Equals(Code, other.Code, StringComparison.Ordinal)
                   && Severity == other.Severity
                   && Source == other.Source
                   && Line == other.Line
                   && Column == other.Column
                   && string.Equals(Message, other.Message, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is RuntimeUiDiagnostic other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Code != null ? Code.GetHashCode() : 0;
                hash = (hash * 397) ^ Line;
                hash = (hash * 397) ^ Column;
                hash = (hash * 397) ^ (int)Source;
                return hash;
            }
        }

        /// <summary>Formats the report as <c>uss:12:5 Warning USS_UNSUPPORTED_PROPERTY: message</c>.</summary>
        public override string ToString()
        {
            string source = Source switch
            {
                RuntimeUiSource.Uxml => "uxml",
                RuntimeUiSource.Uss => "uss",
                RuntimeUiSource.InlineStyle => "style",
                _ => "api"
            };
            return source + ":" + Line + ":" + Column + " " + Severity + " " + Code + ": " + Message;
        }
    }
}
