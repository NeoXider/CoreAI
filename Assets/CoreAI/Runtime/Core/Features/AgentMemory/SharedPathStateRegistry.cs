using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CoreAI.Ai
{
    /// <summary>Retains shared path state while owners, queued waiters or unresolved effects still need it.</summary>
    internal sealed class SharedPathStateRegistry<T> where T : class, new()
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _entries;
        private readonly Func<string, string> _keyNormalizer;
        private readonly Func<T, bool> _retain;
        private readonly int _capacity;

        internal SharedPathStateRegistry(Func<T, bool> retain = null, int capacity = 1024,
            Func<string, string> keyNormalizer = null, StringComparer keyComparer = null)
        {
            _retain = retain;
            _capacity = Math.Max(1, capacity);
            _keyNormalizer = keyNormalizer ?? Path.GetFullPath;
            _entries = new Dictionary<string, Entry>(keyComparer ??
                (Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
        }

        internal sealed class Entry
        {
            internal T State = new();
            internal int Users;
        }

        internal int Count { get { lock (_gate) return _entries.Count; } }

        internal int CountWhere(Func<T, bool> predicate)
        {
            lock (_gate)
            {
                int count = 0;
                foreach (Entry entry in _entries.Values) if (predicate(entry.State)) count++;
                return count;
            }
        }

        internal Lease Acquire(string path)
        {
            string key = _keyNormalizer(path);
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out Entry entry))
                {
                    List<string> retired = new();
                    foreach (KeyValuePair<string, Entry> pair in _entries)
                        if (pair.Value.Users == 0 && _retain?.Invoke(pair.Value.State) != true) retired.Add(pair.Key);
                    foreach (string stale in retired) _entries.Remove(stale);
                    if (_entries.Count >= _capacity)
                        throw new InvalidOperationException("Shared storage path capacity is exhausted by active owners or pending confirmations.");
                    entry = new Entry();
                    _entries.Add(key, entry);
                }
                entry.Users++;
                return new Lease(this, key, entry);
            }
        }

        private void Release(string key, Entry entry)
        {
            lock (_gate)
            {
                entry.Users--;
                if (entry.Users == 0 && _retain?.Invoke(entry.State) != true) _entries.Remove(key);
            }
        }

        internal sealed class Lease : IDisposable
        {
            private SharedPathStateRegistry<T> _owner;
            private readonly string _key;
            private readonly Entry _entry;
            internal Lease(SharedPathStateRegistry<T> owner, string key, Entry entry)
            {
                _owner = owner;
                _key = key;
                _entry = entry;
            }

            internal T State => _entry.State;
            ~Lease() { Dispose(); }
            public void Dispose()
            {
                SharedPathStateRegistry<T> owner = Interlocked.Exchange(ref _owner, null);
                owner?.Release(_key, _entry);
                GC.SuppressFinalize(this);
            }
        }
    }
}
