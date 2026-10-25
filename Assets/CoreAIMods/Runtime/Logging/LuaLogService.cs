using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Ai.Logging
{
    /// <summary>
    /// Thread-safe <see cref="ILuaLogService"/> backed by fixed-capacity ring buffers: one per mod id
    /// plus one global buffer spanning all mods. Mods normally append on the main thread (tick-driven),
    /// while streaming/AI consumers may query from background tasks, so every operation is guarded by
    /// a single lock. Retained mod ids and text have independent limits; evictions inspect only the
    /// bounded mod dictionary. Stored/query entries are copied so callers cannot expand retained text.
    /// <para>
    /// Deliberately has zero Unity-console coupling by default. Passing a non-null
    /// <paramref name="mirrorLogger"/> (see constructor) mirrors <see cref="LuaLogLevel.Error"/> and
    /// <see cref="LuaLogLevel.RuntimeError"/> entries into it as a convenience; every other severity,
    /// and everything when no logger is supplied, stays purely in these buffers.
    /// </para>
    /// </summary>
    public sealed class LuaLogService : ILuaLogService
    {
        /// <summary>Default per-mod ring buffer capacity.</summary>
        public const int DefaultPerModCapacity = 512;

        /// <summary>Default global ring buffer capacity.</summary>
        public const int DefaultGlobalCapacity = 4096;

        private readonly object _gate = new();
        private readonly Dictionary<string, RingBuffer> _perMod = new(StringComparer.Ordinal);
        private readonly RingBuffer _global;
        private readonly int _perModCapacity;
        private readonly IGameLogger _mirrorLogger;
        private readonly bool _mirrorErrorsToUnityConsole;
        private readonly int _maximumRetainedModIds;
        private readonly int _maximumMessageCharacters;
        private readonly long _maximumRetainedCharacters;
        private long _retainedCharacters;
        private long _sequence;

        /// <inheritdoc />
        // WHY: fired outside _gate (see Append), so storage stays uncontended and a slow handler can
        // never stall other threads' appends. The cost is best-effort ordering under concurrent
        // appends — consumers order by LuaLogEntry.Sequence, which is assigned under the lock.
        public event Action<LuaLogEntry> EntryAppended;

        public LuaLogService(int perModCapacity, int globalCapacity, IGameLogger mirrorLogger,
            bool mirrorErrorsToUnityConsole)
            : this(perModCapacity, globalCapacity, mirrorLogger, mirrorErrorsToUnityConsole, 128, 16384, 4 * 1024 * 1024)
        {
        }

        /// <param name="perModCapacity">Ring buffer capacity for each mod id.</param>
        /// <param name="globalCapacity">Ring buffer capacity for the cross-mod global view.</param>
        /// <param name="mirrorLogger">
        /// Optional Unity-facing logger. When supplied, error-severity entries are also written there
        /// (gated by <paramref name="mirrorErrorsToUnityConsole"/>); this service never requires it.
        /// </param>
        /// <param name="mirrorErrorsToUnityConsole">
        /// When true (default) and <paramref name="mirrorLogger"/> is non-null, <see cref="LuaLogLevel.Error"/>
        /// and <see cref="LuaLogLevel.RuntimeError"/> entries are mirrored to it. Print/Warn are never mirrored.
        /// </param>
        public LuaLogService(
            int perModCapacity = DefaultPerModCapacity,
            int globalCapacity = DefaultGlobalCapacity,
            IGameLogger mirrorLogger = null,
            bool mirrorErrorsToUnityConsole = true,
            int maximumRetainedModIds = 128,
            int maximumMessageCharacters = 16384,
            long maximumRetainedCharacters = 4 * 1024 * 1024)
        {
            if (perModCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(perModCapacity));
            }

            if (globalCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(globalCapacity));
            }

            if (maximumRetainedModIds <= 0 || maximumMessageCharacters < 16 || maximumRetainedCharacters <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumRetainedModIds), "Log retention limits must be positive; text limits must allow the truncation marker.");
            }

            _perModCapacity = perModCapacity;
            _global = new RingBuffer(globalCapacity);
            _mirrorLogger = mirrorLogger;
            _mirrorErrorsToUnityConsole = mirrorErrorsToUnityConsole;
            _maximumRetainedModIds = maximumRetainedModIds;
            _maximumMessageCharacters = maximumMessageCharacters;
            _maximumRetainedCharacters = maximumRetainedCharacters;
        }

        /// <summary>Current retained per-mod buffer count.</summary>
        public int RetainedModCount { get { lock (_gate) { return _perMod.Count; } } }

        /// <summary>UTF-16 characters retained across both views; shared entries are counted twice.</summary>
        public long RetainedCharacters { get { lock (_gate) { return _retainedCharacters; } } }

        /// <inheritdoc />
        public void Append(LuaLogEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            entry.ModId ??= "";
            entry.Message = Clamp(entry.Message);
            entry.ScriptName = entry.ScriptName == null ? null : Clamp(entry.ScriptName);

            lock (_gate)
            {
                entry.Sequence = ++_sequence;
                entry.UtcTime = DateTime.UtcNow;

                LuaLogEntry stored = Copy(entry);
                _retainedCharacters += _global.Add(stored);
                RingBuffer modBuffer = GetOrCreatePerModBufferNoLock(stored.ModId);
                _retainedCharacters += modBuffer.Add(stored);
                while (_retainedCharacters > _maximumRetainedCharacters && _perMod.Count > 0)
                {
                    EvictOldestModNoLock();
                }

                while (_retainedCharacters > _maximumRetainedCharacters)
                {
                    _retainedCharacters -= _global.RemoveOldest();
                }
            }

            if (_mirrorErrorsToUnityConsole && _mirrorLogger != null && IsErrorSeverity(entry.Level))
            {
                MirrorToUnityConsole(entry);
            }

            EntryAppended?.Invoke(entry);
        }

        private static bool IsErrorSeverity(LuaLogLevel level)
        {
            return level is LuaLogLevel.Error or LuaLogLevel.RuntimeError;
        }

        private void MirrorToUnityConsole(LuaLogEntry entry)
        {
            try
            {
                string location = entry.ScriptName == null
                    ? ""
                    : entry.Line.HasValue
                        ? $" ({entry.ScriptName}:{entry.Line.Value})"
                        : $" ({entry.ScriptName})";
                _mirrorLogger.LogError(GameLogFeature.CustomA,
                    $"[Lua/{entry.ModId}] {entry.Level}{location}: {entry.Message}");
            }
            catch
            {
                // WHY: A logging mirror must never throw out of a mod's log call and break gameplay.
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<LuaLogEntry> Query(LuaLogQuery query)
        {
            query ??= new LuaLogQuery();
            int maxCount = query.MaxCount > 0 ? query.MaxCount : int.MaxValue;

            List<LuaLogEntry> matched = new();
            lock (_gate)
            {
                RingBuffer source = string.IsNullOrEmpty(query.ModId)
                    ? _global
                    : _perMod.TryGetValue(query.ModId, out RingBuffer buffer)
                        ? buffer
                        : null;

                if (source == null)
                {
                    return Array.Empty<LuaLogEntry>();
                }

                foreach (LuaLogEntry entry in source.EnumerateOldestFirst())
                {
                    if (entry.Sequence <= query.SinceSequence)
                    {
                        continue;
                    }

                    if (query.MinLevel.HasValue && entry.Level < query.MinLevel.Value)
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(query.TextContains) &&
                        (entry.Message == null ||
                         entry.Message.IndexOf(query.TextContains, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        continue;
                    }

                    matched.Add(Copy(entry));
                }
            }

            if (matched.Count > maxCount)
            {
                matched.RemoveRange(0, matched.Count - maxCount);
            }

            return matched;
        }

        /// <inheritdoc />
        public void Clear(string modId = null)
        {
            lock (_gate)
            {
                if (string.IsNullOrEmpty(modId))
                {
                    _global.Clear();
                    _perMod.Clear();
                    _retainedCharacters = 0;

                    return;
                }

                if (_perMod.TryGetValue(modId, out RingBuffer modBuffer))
                {
                    _retainedCharacters -= modBuffer.Characters;
                    _perMod.Remove(modId);
                }
            }
        }

        private RingBuffer GetOrCreatePerModBufferNoLock(string modId)
        {
            if (!_perMod.TryGetValue(modId, out RingBuffer buffer))
            {
                if (_perMod.Count >= _maximumRetainedModIds)
                {
                    EvictOldestModNoLock();
                }

                buffer = new RingBuffer(_perModCapacity);
                _perMod[modId] = buffer;
            }

            return buffer;
        }

        private void EvictOldestModNoLock()
        {
            string oldest = null;
            long sequence = long.MaxValue;
            foreach (KeyValuePair<string, RingBuffer> pair in _perMod)
            {
                if (pair.Value.LastSequence < sequence)
                {
                    oldest = pair.Key;
                    sequence = pair.Value.LastSequence;
                }
            }

            if (oldest != null)
            {
                _retainedCharacters -= _perMod[oldest].Characters;
                _perMod.Remove(oldest);
            }
        }

        private string Clamp(string text)
        {
            const string marker = "...[truncated]";
            text ??= "";
            return text.Length <= _maximumMessageCharacters
                ? text : text.Substring(0, _maximumMessageCharacters - marker.Length) + marker;
        }

        private static LuaLogEntry Copy(LuaLogEntry entry)
        {
            return new LuaLogEntry
            {
                Sequence = entry.Sequence, UtcTime = entry.UtcTime, GameTime = entry.GameTime,
                ModId = entry.ModId, Level = entry.Level, Message = entry.Message,
                ScriptName = entry.ScriptName, Line = entry.Line
            };
        }

        /// <summary>
        /// Fixed-capacity circular buffer of <see cref="LuaLogEntry"/> references. Once full, each
        /// <see cref="Add"/> overwrites the oldest slot in place — O(1), no allocation, no resizing.
        /// </summary>
        private sealed class RingBuffer
        {
            private readonly LuaLogEntry[] _items;
            private int _start;
            private int _count;
            public long Characters { get; private set; }
            public long LastSequence { get; private set; }

            public RingBuffer(int capacity)
            {
                _items = new LuaLogEntry[capacity];
            }

            public long Add(LuaLogEntry entry)
            {
                long before = Characters;
                int index = (_start + _count) % _items.Length;
                if (_count == _items.Length)
                {
                    Characters -= Size(_items[index]);
                }

                _items[index] = entry;
                Characters += Size(entry);
                LastSequence = entry.Sequence;
                if (_count == _items.Length)
                {
                    _start = (_start + 1) % _items.Length;
                }
                else
                {
                    _count++;
                }

                return Characters - before;
            }

            public long RemoveOldest()
            {
                if (_count == 0)
                {
                    return 0;
                }

                long removed = Size(_items[_start]);
                _items[_start] = null;
                _start = (_start + 1) % _items.Length;
                _count--;
                Characters -= removed;
                return removed;
            }

            private static long Size(LuaLogEntry entry)
            {
                return (long)entry.ModId.Length + entry.Message.Length + (entry.ScriptName?.Length ?? 0);
            }

            public void Clear()
            {
                Array.Clear(_items, 0, _items.Length);
                _start = 0;
                _count = 0;
                Characters = 0;
            }

            public IEnumerable<LuaLogEntry> EnumerateOldestFirst()
            {
                for (int i = 0; i < _count; i++)
                {
                    yield return _items[(_start + i) % _items.Length];
                }
            }
        }
    }
}
