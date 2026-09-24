using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityEngine
{
    /// <summary>Unity's log entry categories, with Unity's numeric values.</summary>
    public enum LogType
    {
        Error = 0,
        Assert = 1,
        Warning = 2,
        Log = 3,
        Exception = 4
    }

    /// <summary>
    /// Portable twin of <c>UnityEngine.Debug</c>: formats the message the way Unity's default logger does
    /// (null prints as "Null", <see cref="IFormattable"/> values use the invariant culture), writes it to
    /// the console and hands it to <see cref="PortableLogScope"/>, which reproduces the Unity Test
    /// Framework rule that an unexpected error, assert or exception log fails the running test.
    /// </summary>
    public static class Debug
    {
        public static void Log(object message)
        {
            PortableLogScope.Write(LogType.Log, Format(message));
        }

        public static void Log(object message, Object context)
        {
            PortableLogScope.Write(LogType.Log, Format(message));
        }

        public static void LogFormat(string format, params object[] args)
        {
            PortableLogScope.Write(LogType.Log, string.Format(CultureInfo.InvariantCulture, format, args));
        }

        public static void LogWarning(object message)
        {
            PortableLogScope.Write(LogType.Warning, Format(message));
        }

        public static void LogWarning(object message, Object context)
        {
            PortableLogScope.Write(LogType.Warning, Format(message));
        }

        public static void LogWarningFormat(string format, params object[] args)
        {
            PortableLogScope.Write(LogType.Warning, string.Format(CultureInfo.InvariantCulture, format, args));
        }

        public static void LogError(object message)
        {
            PortableLogScope.Write(LogType.Error, Format(message));
        }

        public static void LogError(object message, Object context)
        {
            PortableLogScope.Write(LogType.Error, Format(message));
        }

        public static void LogErrorFormat(string format, params object[] args)
        {
            PortableLogScope.Write(LogType.Error, string.Format(CultureInfo.InvariantCulture, format, args));
        }

        public static void LogException(Exception exception)
        {
            PortableLogScope.Write(LogType.Exception, FormatException(exception));
        }

        public static void LogException(Exception exception, Object context)
        {
            PortableLogScope.Write(LogType.Exception, FormatException(exception));
        }

        private static string Format(object message)
        {
            if (message == null)
            {
                return "Null";
            }

            return message is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : message.ToString();
        }

        private static string FormatException(Exception exception)
        {
            if (exception == null)
            {
                return "Null";
            }

            return exception.GetType().Name + ": " + exception.Message;
        }
    }

    /// <summary>
    /// Per-test log bookkeeping that mirrors the Unity Test Framework's <c>LogScope</c>: every log is
    /// recorded; Error, Assert and Exception entries fail the test unless a matching
    /// <c>LogAssert.Expect</c> consumed them (expectations are matched in order against the log stream,
    /// exact message or regex) or <c>LogAssert.ignoreFailingMessages</c> is set; an expectation that
    /// never matched fails the test too. The portable test assembly opens a scope before each test and
    /// evaluates it after the test's TearDown.
    /// </summary>
    public static class PortableLogScope
    {
        private static readonly object Gate = new object();
        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly List<Expectation> Expectations = new List<Expectation>();
        private static bool _active;

        /// <summary>When set, error logs do not fail the current test (LogAssert.ignoreFailingMessages).</summary>
        public static bool IgnoreFailingMessages { get; set; }

        /// <summary>Starts a fresh scope for the next test.</summary>
        public static void Begin()
        {
            lock (Gate)
            {
                Entries.Clear();
                Expectations.Clear();
                IgnoreFailingMessages = false;
                _active = true;
            }
        }

        /// <summary>
        /// Closes the current scope and returns the failure Unity would report, or null when the test's
        /// logs satisfy the Unity Test Framework rules.
        /// </summary>
        public static string End()
        {
            lock (Gate)
            {
                string failure = Evaluate(endOfScope: true);
                Entries.Clear();
                Expectations.Clear();
                IgnoreFailingMessages = false;
                _active = false;
                return failure;
            }
        }

        /// <summary>Evaluates the scope so far without closing it (LogAssert.NoUnexpectedReceived).</summary>
        public static string EvaluateNow()
        {
            lock (Gate)
            {
                return Evaluate(endOfScope: false);
            }
        }

        internal static void Write(LogType type, string message)
        {
            Console.WriteLine("[Unity " + type + "] " + message);
            lock (Gate)
            {
                if (!_active)
                {
                    return;
                }

                Entries.Add(new Entry(type, message, IsFailing(type) && !IgnoreFailingMessages));
            }
        }

        internal static void Expect(LogType? type, string message, System.Text.RegularExpressions.Regex regex)
        {
            lock (Gate)
            {
                Expectations.Add(new Expectation(type, message, regex));
            }
        }

        private static bool IsFailing(LogType type)
        {
            return type == LogType.Error || type == LogType.Assert || type == LogType.Exception;
        }

        private static string Evaluate(bool endOfScope)
        {
            int next = 0;
            foreach (Entry entry in Entries)
            {
                if (next >= Expectations.Count)
                {
                    break;
                }

                if (entry.Handled)
                {
                    continue;
                }

                if (Expectations[next].Matches(entry))
                {
                    entry.Handled = true;
                    entry.Failing = false;
                    next++;
                }
            }

            Expectations.RemoveRange(0, next);
            foreach (Entry entry in Entries)
            {
                if (entry.Failing)
                {
                    return "Unhandled log message: '[" + entry.Type + "] " + entry.Message +
                           "'. Use UnityEngine.TestTools.LogAssert.Expect";
                }
            }

            if (endOfScope && Expectations.Count > 0)
            {
                Expectation missing = Expectations[0];
                return "Expected log did not appear: [" + (missing.Type?.ToString() ?? "Any") + "] " +
                       (missing.Message ?? missing.Regex?.ToString());
            }

            return null;
        }

        private sealed class Entry
        {
            public Entry(LogType type, string message, bool failing)
            {
                Type = type;
                Message = message;
                Failing = failing;
            }

            public LogType Type { get; }

            public string Message { get; }

            public bool Failing { get; set; }

            public bool Handled { get; set; }
        }

        private sealed class Expectation
        {
            public Expectation(LogType? type, string message, System.Text.RegularExpressions.Regex regex)
            {
                Type = type;
                Message = message;
                Regex = regex;
            }

            public LogType? Type { get; }

            public string Message { get; }

            public System.Text.RegularExpressions.Regex Regex { get; }

            public bool Matches(Entry entry)
            {
                if (Type.HasValue && Type.Value != entry.Type)
                {
                    return false;
                }

                if (Message != null)
                {
                    return string.Equals(Message, entry.Message, StringComparison.Ordinal);
                }

                return Regex == null || Regex.IsMatch(entry.Message);
            }
        }
    }
}

namespace UnityEngine.TestTools
{
    /// <summary>
    /// Portable twin of the Unity Test Framework's <c>LogAssert</c>, backed by
    /// <see cref="UnityEngine.PortableLogScope"/>.
    /// </summary>
    public static class LogAssert
    {
        public static bool ignoreFailingMessages
        {
            get => PortableLogScope.IgnoreFailingMessages;
            set => PortableLogScope.IgnoreFailingMessages = value;
        }

        public static void Expect(LogType type, string message)
        {
            PortableLogScope.Expect(type, message, null);
        }

        public static void Expect(LogType type, System.Text.RegularExpressions.Regex message)
        {
            PortableLogScope.Expect(type, null, message);
        }

        public static void Expect(string message)
        {
            PortableLogScope.Expect(null, message, null);
        }

        public static void Expect(System.Text.RegularExpressions.Regex message)
        {
            PortableLogScope.Expect(null, null, message);
        }

        public static void NoUnexpectedReceived()
        {
            string failure = PortableLogScope.EvaluateNow();
            if (failure != null)
            {
                throw new PortableUnhandledLogMessageException(failure);
            }
        }
    }

    /// <summary>Raised by <see cref="LogAssert.NoUnexpectedReceived"/> like Unity's own failure.</summary>
    public sealed class PortableUnhandledLogMessageException : System.Exception
    {
        public PortableUnhandledLogMessageException(string message) : base(message)
        {
        }
    }
}
