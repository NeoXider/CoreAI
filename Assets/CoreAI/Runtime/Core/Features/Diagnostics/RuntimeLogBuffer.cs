using System;
using System.Collections.Generic;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// Bounded, thread-safe ring of engine log entries: a fixed number of entries, each message and stack trace
    /// clamped, so a log storm in a long session cannot grow memory. Appends come from any thread (Unity's
    /// threaded log callback); reads take a snapshot under the same lock.
    /// </summary>
    public sealed class RuntimeLogBuffer : IRuntimeLogSource
    {
        /// <summary>Default number of retained entries.</summary>
        public const int DefaultCapacity = 1000;

        /// <summary>Default cap on one message, in characters.</summary>
        public const int DefaultMaxMessageChars = 4000;

        /// <summary>Default cap on one stack trace, in characters.</summary>
        public const int DefaultMaxStackTraceChars = 4000;

        private const string TruncationMarker = "...[truncated]";

        private readonly object _gate = new();
        private readonly RuntimeLogEntry[] _items;
        private readonly int _maxMessageChars;
        private readonly int _maxStackTraceChars;
        private readonly RuntimeLogLevel _minStackTraceLevel;
        private readonly Func<DateTime> _clock;
        private int _start;
        private int _count;
        private long _sequence;
        private long _info;
        private long _warnings;
        private long _errors;
        private long _exceptions;

        /// <param name="capacity">Retained entries; older ones are evicted first.</param>
        /// <param name="maxMessageChars">Cap on one message (at least 32).</param>
        /// <param name="maxStackTraceChars">Cap on one stack trace (0 keeps none).</param>
        /// <param name="minStackTraceLevel">Lowest severity whose stack trace is kept; the rest drop theirs.</param>
        /// <param name="clock">UTC clock; null uses <see cref="DateTime.UtcNow"/>.</param>
        public RuntimeLogBuffer(int capacity = DefaultCapacity, int maxMessageChars = DefaultMaxMessageChars,
            int maxStackTraceChars = DefaultMaxStackTraceChars,
            RuntimeLogLevel minStackTraceLevel = RuntimeLogLevel.Warning, Func<DateTime> clock = null)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (maxMessageChars < 32) throw new ArgumentOutOfRangeException(nameof(maxMessageChars));
            if (maxStackTraceChars < 0) throw new ArgumentOutOfRangeException(nameof(maxStackTraceChars));
            _items = new RuntimeLogEntry[capacity];
            _maxMessageChars = maxMessageChars;
            _maxStackTraceChars = maxStackTraceChars;
            _minStackTraceLevel = minStackTraceLevel;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>Retained entries right now.</summary>
        public int Count { get { lock (_gate) return _count; } }

        /// <summary>Capacity in entries.</summary>
        public int Capacity => _items.Length;

        /// <inheritdoc />
        public long LatestSequence { get { lock (_gate) return _sequence; } }

        /// <inheritdoc />
        public long OldestRetainedSequence
        {
            get
            {
                lock (_gate) return _count == 0 ? _sequence + 1 : _items[_start].Sequence;
            }
        }

        /// <inheritdoc />
        public RuntimeLogCounts Counts
        {
            get { lock (_gate) return new RuntimeLogCounts(_info, _warnings, _errors, _exceptions); }
        }

        /// <summary>Text that replaces a redacted secret.</summary>
        public const string RedactedText = "<redacted>";

        private string[] _secrets = Array.Empty<string>();

        /// <summary>
        /// Replaces <paramref name="secret"/> with <see cref="RedactedText"/> in every entry appended from now on.
        /// WHY: the log is readable by in-game agents whose prompts may go to a cloud provider, so a bearer token a
        /// server prints for the developer must never reach them. Secrets shorter than 8 characters are ignored.
        /// </summary>
        public void AddRedaction(string secret)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < 8)
            {
                return;
            }

            lock (_gate)
            {
                if (Array.IndexOf(_secrets, secret) >= 0) return;
                string[] next = new string[_secrets.Length + 1];
                Array.Copy(_secrets, next, _secrets.Length);
                next[_secrets.Length] = secret;
                _secrets = next;
            }
        }

        /// <summary>Appends one entry and returns it; safe from any thread.</summary>
        public RuntimeLogEntry Append(RuntimeLogLevel level, string message, string stackTrace = null)
        {
            string[] secrets = System.Threading.Volatile.Read(ref _secrets);
            string clampedMessage = Clamp(Redact(message ?? "", secrets), _maxMessageChars);
            string clampedStack = level >= _minStackTraceLevel && _maxStackTraceChars > 0
                ? Clamp(Redact(stackTrace ?? "", secrets), _maxStackTraceChars)
                : "";
            lock (_gate)
            {
                RuntimeLogEntry entry = new(++_sequence, _clock(), level, clampedMessage, clampedStack);
                int index = (_start + _count) % _items.Length;
                _items[index] = entry;
                if (_count == _items.Length)
                {
                    _start = (_start + 1) % _items.Length;
                }
                else
                {
                    _count++;
                }

                switch (level)
                {
                    case RuntimeLogLevel.Warning: _warnings++; break;
                    case RuntimeLogLevel.Error: _errors++; break;
                    case RuntimeLogLevel.Exception: _exceptions++; break;
                    default: _info++; break;
                }

                return entry;
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<RuntimeLogEntry> Read(RuntimeLogQuery query)
        {
            query ??= new RuntimeLogQuery();
            string contains = string.IsNullOrEmpty(query.Contains) ? null : query.Contains;
            List<RuntimeLogEntry> matched = new();
            lock (_gate)
            {
                for (int offset = 0; offset < _count; offset++)
                {
                    RuntimeLogEntry entry = _items[(_start + offset) % _items.Length];
                    if (entry.Sequence <= query.SinceSequence || entry.Level < query.MinLevel)
                    {
                        continue;
                    }

                    if (contains != null && entry.Message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    matched.Add(entry);
                }
            }

            if (query.MaxCount > 0 && matched.Count > query.MaxCount)
            {
                matched.RemoveRange(0, matched.Count - query.MaxCount);
            }

            return matched;
        }

        /// <summary>Drops every retained entry; sequence numbers and totals keep counting.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                Array.Clear(_items, 0, _items.Length);
                _start = 0;
                _count = 0;
            }
        }

        private static string Redact(string text, string[] secrets)
        {
            for (int index = 0; index < secrets.Length; index++)
            {
                if (text.IndexOf(secrets[index], StringComparison.Ordinal) >= 0)
                {
                    text = text.Replace(secrets[index], RedactedText);
                }
            }

            return text;
        }

        private static string Clamp(string text, int max)
        {
            if (text.Length <= max)
            {
                return text;
            }

            int keep = Math.Max(0, max - TruncationMarker.Length);
            // WHY: never split a surrogate pair at the cut.
            if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
            {
                keep--;
            }

            return text.Substring(0, keep) + TruncationMarker;
        }
    }
}
