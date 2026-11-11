using System;
using System.Collections.Generic;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// The outcome of a host or screen operation: success, a stable error code when it failed outright,
    /// and every report the parsers and the cascade raised (an operation can succeed with reports).
    /// </summary>
    public sealed class RuntimeUiResult
    {
        private static readonly IReadOnlyList<RuntimeUiDiagnostic> None = Array.Empty<RuntimeUiDiagnostic>();

        private RuntimeUiResult(bool success, string errorCode, string error, RuntimeUiScreen screen,
            IReadOnlyList<RuntimeUiDiagnostic> diagnostics)
        {
            Success = success;
            ErrorCode = errorCode;
            Error = error;
            Screen = screen;
            Diagnostics = diagnostics ?? None;
        }

        /// <summary>True when the operation took effect.</summary>
        public bool Success { get; }

        /// <summary>A <see cref="RuntimeUiDiagnosticCodes"/> value when <see cref="Success"/> is false.</summary>
        public string ErrorCode { get; }

        /// <summary>Why it failed.</summary>
        public string Error { get; }

        /// <summary>The screen the operation created or changed, when there is one.</summary>
        public RuntimeUiScreen Screen { get; }

        /// <summary>Reports raised by this operation.</summary>
        public IReadOnlyList<RuntimeUiDiagnostic> Diagnostics { get; }

        /// <summary>A success.</summary>
        public static RuntimeUiResult Ok(RuntimeUiScreen screen, IReadOnlyList<RuntimeUiDiagnostic> diagnostics)
        {
            return new RuntimeUiResult(true, null, null, screen, diagnostics);
        }

        /// <summary>A failure with a stable code.</summary>
        public static RuntimeUiResult Fail(string errorCode, string error, RuntimeUiScreen screen = null,
            IReadOnlyList<RuntimeUiDiagnostic> diagnostics = null)
        {
            return new RuntimeUiResult(false, errorCode, error, screen, diagnostics);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Success
                ? "ok (" + Diagnostics.Count + " reports)"
                : ErrorCode + ": " + Error;
        }
    }

    /// <summary>How a new screen starts.</summary>
    public sealed class RuntimeUiScreenOptions
    {
        /// <summary>Screens with a higher order draw on top; ties keep stack and creation order.</summary>
        public int SortOrder { get; set; }

        /// <summary>Whether the screen is shown right away.</summary>
        public bool Visible { get; set; } = true;

        /// <summary>An owner tag (a mod id, say) the host keeps for bookkeeping and bulk removal.</summary>
        public string Owner { get; set; }
    }
}
