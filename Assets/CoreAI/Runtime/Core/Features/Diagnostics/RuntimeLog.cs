using System;
using System.Collections.Generic;

namespace CoreAI.Diagnostics
{
    /// <summary>Severity of an engine log entry, ordered so a minimum level filters by comparison.</summary>
    public enum RuntimeLogLevel
    {
        /// <summary>An ordinary message (<c>Debug.Log</c>).</summary>
        Info = 0,

        /// <summary>A warning (<c>Debug.LogWarning</c>).</summary>
        Warning = 1,

        /// <summary>An error or a failed assertion (<c>Debug.LogError</c>, <c>Debug.LogAssertion</c>).</summary>
        Error = 2,

        /// <summary>An exception that reached the engine log (<c>Debug.LogException</c>, an unhandled exception).</summary>
        Exception = 3
    }

    /// <summary>One captured engine log entry. Immutable; the sequence number is the reader's cursor.</summary>
    public sealed class RuntimeLogEntry
    {
        /// <param name="sequence">Monotonic number assigned by the buffer, starting at 1.</param>
        /// <param name="utcTime">When the entry was captured.</param>
        /// <param name="level">Severity.</param>
        /// <param name="message">The message, possibly clamped by the buffer.</param>
        /// <param name="stackTrace">The stack trace, empty when the buffer keeps none for this level.</param>
        public RuntimeLogEntry(long sequence, DateTime utcTime, RuntimeLogLevel level, string message, string stackTrace)
        {
            Sequence = sequence;
            UtcTime = utcTime;
            Level = level;
            Message = message ?? "";
            StackTrace = stackTrace ?? "";
        }

        /// <summary>Monotonic number assigned by the buffer, starting at 1.</summary>
        public long Sequence { get; }

        /// <summary>When the entry was captured (UTC).</summary>
        public DateTime UtcTime { get; }

        /// <summary>Severity.</summary>
        public RuntimeLogLevel Level { get; }

        /// <summary>The message, possibly clamped by the buffer.</summary>
        public string Message { get; }

        /// <summary>The stack trace; empty when none was kept.</summary>
        public string StackTrace { get; }
    }

    /// <summary>What a reader asks a <see cref="IRuntimeLogSource"/> for.</summary>
    public sealed class RuntimeLogQuery
    {
        /// <summary>Only entries with a sequence greater than this; 0 reads from the oldest retained entry.</summary>
        public long SinceSequence { get; set; }

        /// <summary>Lowest severity returned.</summary>
        public RuntimeLogLevel MinLevel { get; set; } = RuntimeLogLevel.Info;

        /// <summary>At most this many entries, the newest matches kept; 0 or less means all.</summary>
        public int MaxCount { get; set; } = 50;

        /// <summary>Case-insensitive substring the message must contain; null or empty matches every entry.</summary>
        public string Contains { get; set; }
    }

    /// <summary>Totals per severity since the source started, including entries the ring buffer already evicted.</summary>
    public readonly struct RuntimeLogCounts
    {
        /// <param name="info">Info entries seen.</param>
        /// <param name="warnings">Warnings seen.</param>
        /// <param name="errors">Errors and failed assertions seen.</param>
        /// <param name="exceptions">Exceptions seen.</param>
        public RuntimeLogCounts(long info, long warnings, long errors, long exceptions)
        {
            Info = info;
            Warnings = warnings;
            Errors = errors;
            Exceptions = exceptions;
        }

        /// <summary>Info entries seen.</summary>
        public long Info { get; }

        /// <summary>Warnings seen.</summary>
        public long Warnings { get; }

        /// <summary>Errors and failed assertions seen.</summary>
        public long Errors { get; }

        /// <summary>Exceptions seen.</summary>
        public long Exceptions { get; }
    }

    /// <summary>
    /// Read side of a bounded engine log (Unity's console stream in a Unity host). Debug surfaces - the MCP
    /// <c>get_unity_logs</c> tool, the in-game <c>get_mod_logs</c> <c>source=unity</c> mode - read it with a
    /// cursor (<see cref="RuntimeLogQuery.SinceSequence"/>) so a client receives only what is new. Safe to call
    /// from any thread.
    /// </summary>
    public interface IRuntimeLogSource
    {
        /// <summary>Sequence of the newest entry ever appended; 0 before the first.</summary>
        long LatestSequence { get; }

        /// <summary>Sequence of the oldest entry still retained; entries below it were evicted.</summary>
        long OldestRetainedSequence { get; }

        /// <summary>Totals per severity since the source started.</summary>
        RuntimeLogCounts Counts { get; }

        /// <summary>Matching entries, oldest first, capped to the newest <see cref="RuntimeLogQuery.MaxCount"/>.</summary>
        IReadOnlyList<RuntimeLogEntry> Read(RuntimeLogQuery query);
    }

    /// <summary>Parses the level names the debug tools accept.</summary>
    public static class RuntimeLogLevels
    {
        /// <summary>The accepted names, lowest first, for error messages and schemas.</summary>
        public const string Names = "info, warning, error, exception";

        /// <summary>
        /// Reads <c>info</c>/<c>log</c>/<c>print</c>, <c>warning</c>/<c>warn</c>, <c>error</c>/<c>assert</c> and
        /// <c>exception</c>/<c>runtime_error</c> (case-insensitive). Null or blank returns <paramref name="fallback"/>.
        /// </summary>
        public static bool TryParse(string text, RuntimeLogLevel fallback, out RuntimeLogLevel level)
        {
            level = fallback;
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "info":
                case "log":
                case "print":
                    level = RuntimeLogLevel.Info;
                    return true;
                case "warning":
                case "warn":
                    level = RuntimeLogLevel.Warning;
                    return true;
                case "error":
                case "assert":
                    level = RuntimeLogLevel.Error;
                    return true;
                case "exception":
                case "runtime_error":
                    level = RuntimeLogLevel.Exception;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The short upper-case tag a formatted line carries.</summary>
        public static string Tag(RuntimeLogLevel level)
        {
            switch (level)
            {
                case RuntimeLogLevel.Warning:
                    return "WARN";
                case RuntimeLogLevel.Error:
                    return "ERROR";
                case RuntimeLogLevel.Exception:
                    return "EXCEPTION";
                default:
                    return "INFO";
            }
        }
    }
}
