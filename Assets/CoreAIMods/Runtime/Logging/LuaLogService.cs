using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Ai.Logging
{
    /// <summary>
    /// Thread-safe <see cref="ILuaLogService"/> backed by fixed-capacity ring buffers: one per mod id
    /// plus one global buffer spanning all mods. Mods normally append on the main thread (tick-driven),
    /// while streaming/AI consumers may query from background tasks, so every operation is guarded by
    /// a single lock; appends and queries are O(1)/O(capacity) with no allocations beyond the
    /// <see cref="LuaLogEntry"/> itself (and the small result list a query builds).
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
        private long _sequence;

        /// <inheritdoc />
        public event Action<LuaLogEntry> EntryAppended;

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
            bool mirrorErrorsToUnityConsole = true)
        {
            if (perModCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(perModCapacity));
            }

            if (globalCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(globalCapacity));
            }

            _perModCapacity = perModCapacity;
            _global = new RingBuffer(globalCapacity);
            _mirrorLogger = mirrorLogger;
            _mirrorErrorsToUnityConsole = mirrorErrorsToUnityConsole;
        }

        /// <inheritdoc />
        public void Append(LuaLogEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            entry.ModId ??= "";
            entry.Message ??= "";

            lock (_gate)
            {
                entry.Sequence = ++_sequence;
                entry.UtcTime = DateTime.UtcNow;

                _global.Add(entry);
                GetOrCreatePerModBufferNoLock(entry.ModId).Add(entry);
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

                    matched.Add(entry);
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
                    foreach (RingBuffer buffer in _perMod.Values)
                    {
                        buffer.Clear();
                    }

                    return;
                }

                if (_perMod.TryGetValue(modId, out RingBuffer modBuffer))
                {
                    modBuffer.Clear();
                }
            }
        }

        private RingBuffer GetOrCreatePerModBufferNoLock(string modId)
        {
            if (!_perMod.TryGetValue(modId, out RingBuffer buffer))
            {
                buffer = new RingBuffer(_perModCapacity);
                _perMod[modId] = buffer;
            }

            return buffer;
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

            public RingBuffer(int capacity)
            {
                _items = new LuaLogEntry[capacity];
            }

            public void Add(LuaLogEntry entry)
            {
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
            }

            public void Clear()
            {
                Array.Clear(_items, 0, _items.Length);
                _start = 0;
                _count = 0;
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
