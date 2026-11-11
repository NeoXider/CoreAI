using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.DataStores
{
    /// <summary>
    /// The data of one namespace in memory, over an <see cref="IRbxDataStorePersistence"/>: every store is read on
    /// first use of the namespace, requests read and write memory at once, and changed stores are written back
    /// later and coalesced (<see cref="RbxDataStoreOptions.WriteDelaySeconds"/>, at most
    /// <see cref="RbxDataStoreOptions.MaxFilesWrittenPerFrame"/> per <see cref="FlushDue"/>), or all at once by
    /// <see cref="FlushAll"/> when the world closes. The world's storage caps are checked on every write.
    /// </summary>
    /// <remarks>
    /// A store whose file was unreadable is set aside by the storage, starts empty, and its first request fails
    /// with Roblox's 501 so the script learns; later requests work on the empty store. A failed write keeps the
    /// store dirty, is tried again after <see cref="RbxDataStoreOptions.RetryDelaySeconds"/>, and until a write
    /// succeeds new writes are refused with Roblox's 502, because the data would not survive.
    /// </remarks>
    public sealed class RbxDataStoreDatabase
    {
        private sealed class Store
        {
            public Store(string name, string scope, string fileId)
            {
                Name = name;
                Scope = scope;
                FileId = fileId;
            }

            public string Name { get; }
            public string Scope { get; }
            public string FileId { get; }
            public readonly Dictionary<string, RbxDataStoreEntry> Entries = new(StringComparer.Ordinal);
            public readonly List<string> Order = new();
            public bool Dirty;
            public double DirtySince;
        }

        private readonly string _namespace;
        private readonly IRbxDataStorePersistence _persistence;
        private readonly RbxDataStoreOptions _options;
        private readonly Func<double> _clock;
        private readonly Action<string> _report;
        private readonly Dictionary<string, Store> _stores = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _corruptFiles = new(StringComparer.Ordinal);
        private readonly List<Store> _dirty = new();
        private bool _loaded;
        private long _storedCharacters;
        private int _keyCount;
        private double _retryAt;

        /// <summary>Creates the namespace; nothing is read until the first request.</summary>
        public RbxDataStoreDatabase(string dataNamespace, IRbxDataStorePersistence persistence, RbxDataStoreOptions options,
            Func<double> clock, Action<string> report)
        {
            _namespace = dataNamespace ?? string.Empty;
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _report = report;
        }

        /// <summary>The namespace this data belongs to.</summary>
        public string Namespace => _namespace;

        /// <summary>Characters kept over all stores (what <see cref="RbxDataStoreOptions.MaxStoredCharactersPerWorld"/> limits).</summary>
        public long StoredCharacters
        {
            get
            {
                EnsureLoaded();
                return _storedCharacters;
            }
        }

        /// <summary>Keys kept over all stores.</summary>
        public int KeyCount
        {
            get
            {
                EnsureLoaded();
                return _keyCount;
            }
        }

        /// <summary>Stores with at least one key.</summary>
        public int StoreCount
        {
            get
            {
                EnsureLoaded();
                int count = 0;
                foreach (Store store in _stores.Values)
                {
                    if (store.Entries.Count > 0)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>True while a change waits to be written.</summary>
        public bool HasPendingWrites => _dirty.Count > 0;

        /// <summary>Why the last write to storage failed; empty after a write succeeds.</summary>
        public string WriteFailure { get; private set; } = string.Empty;

        /// <summary>Reads the namespace now if it was not read yet (the first request does this on its own).</summary>
        public void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            IReadOnlyList<RbxDataStoreLoadResult> results;
            try
            {
                results = _persistence.LoadNamespace(_namespace);
            }
            catch (Exception ex)
            {
                WriteFailure = Describe(ex);
                _report?.Invoke("[DataStoreService] reading the stored data failed; this session starts without it and "
                                + "writes are refused until storage works: " + WriteFailure);
                _retryAt = _clock() + _options.RetryDelaySeconds;
                return;
            }

            for (int index = 0; index < results.Count; index++)
            {
                RbxDataStoreLoadResult result = results[index];
                if (result.Status == RbxDataStoreLoadStatus.Corrupt || result.File == null)
                {
                    _corruptFiles[result.FileId] = result.Problem;
                    _report?.Invoke("[DataStoreService] a stored data store could not be read and was set aside; it "
                                    + "starts empty: " + result.Problem);
                    continue;
                }

                Store store = new(result.File.Name, result.File.Scope, result.FileId);
                for (int entryIndex = 0; entryIndex < result.File.Entries.Count; entryIndex++)
                {
                    RbxDataStoreEntry entry = result.File.Entries[entryIndex];
                    if (entry == null || string.IsNullOrEmpty(entry.Key) || store.Entries.ContainsKey(entry.Key))
                    {
                        continue;
                    }

                    store.Entries.Add(entry.Key, entry);
                    store.Order.Add(entry.Key);
                    _storedCharacters += entry.StoredCharacters;
                    _keyCount++;
                }

                _stores[result.FileId] = store;
            }
        }

        /// <summary>A copy of the entry at <paramref name="key"/>, or null when the key has no value.</summary>
        public RbxDataStoreEntry Read(string name, string scope, string key)
        {
            Store store = Open(name, scope);
            return store.Entries.TryGetValue(key, out RbxDataStoreEntry entry) ? entry.Copy() : null;
        }

        /// <summary>
        /// Writes <paramref name="valueJson"/> at <paramref name="key"/> with its user ids and metadata, and returns a
        /// copy of the new entry. Throws the Roblox-worded error when the world's storage is full or storage fails.
        /// </summary>
        public RbxDataStoreEntry Write(string name, string scope, string key, string valueJson, long[] userIds,
            string metadataJson, long nowMilliseconds)
        {
            Store store = Open(name, scope);
            RequireWritable();
            store.Entries.TryGetValue(key, out RbxDataStoreEntry previous);
            RbxDataStoreEntry next = new()
            {
                Key = key,
                ValueJson = valueJson ?? "null",
                CreatedMilliseconds = previous?.CreatedMilliseconds ?? nowMilliseconds,
                UpdatedMilliseconds = nowMilliseconds,
                Version = (previous?.Version ?? 0L) + 1L,
                UserIds = userIds ?? Array.Empty<long>(),
                MetadataJson = string.IsNullOrEmpty(metadataJson) ? "{}" : metadataJson
            };

            long characters = _storedCharacters - (previous?.StoredCharacters ?? 0L) + next.StoredCharacters;
            if (characters > _options.MaxStoredCharactersPerWorld)
            {
                throw RbxDataStoreErrors.StorageFull("characters", _options.MaxStoredCharactersPerWorld);
            }

            if (previous == null)
            {
                if (_keyCount + 1 > _options.MaxKeysPerWorld)
                {
                    throw RbxDataStoreErrors.StorageFull("keys", _options.MaxKeysPerWorld);
                }

                if (store.Entries.Count == 0 && StoreCount + 1 > _options.MaxStoresPerWorld)
                {
                    throw RbxDataStoreErrors.StorageFull("data stores", _options.MaxStoresPerWorld);
                }

                store.Order.Add(key);
                _keyCount++;
            }

            store.Entries[key] = next;
            _storedCharacters = characters;
            MarkDirty(store);
            return next.Copy();
        }

        /// <summary>Removes <paramref name="key"/>; returns a copy of what it held, or null when it held nothing.</summary>
        public RbxDataStoreEntry Remove(string name, string scope, string key)
        {
            Store store = Open(name, scope);
            if (!store.Entries.TryGetValue(key, out RbxDataStoreEntry previous))
            {
                return null;
            }

            RequireWritable();
            store.Entries.Remove(key);
            store.Order.Remove(key);
            _storedCharacters -= previous.StoredCharacters;
            _keyCount--;
            MarkDirty(store);
            return previous.Copy();
        }

        /// <summary>
        /// Writes the stores whose first unsaved change is at least <see cref="RbxDataStoreOptions.WriteDelaySeconds"/>
        /// old, at most <see cref="RbxDataStoreOptions.MaxFilesWrittenPerFrame"/>; returns how many were written.
        /// </summary>
        public int FlushDue()
        {
            if (_dirty.Count == 0)
            {
                return 0;
            }

            double now = _clock();
            if (now < _retryAt)
            {
                return 0;
            }

            int written = 0;
            int limit = Math.Max(1, _options.MaxFilesWrittenPerFrame);
            for (int index = 0; index < _dirty.Count && written < limit;)
            {
                Store store = _dirty[index];
                if (now - store.DirtySince < _options.WriteDelaySeconds)
                {
                    index++;
                    continue;
                }

                if (!Save(store))
                {
                    return written;
                }

                _dirty.RemoveAt(index);
                written++;
            }

            return written;
        }

        /// <summary>Writes every pending change now (the world is closing); returns false when one could not be written.</summary>
        public bool FlushAll()
        {
            bool allWritten = true;
            for (int index = 0; index < _dirty.Count;)
            {
                if (Save(_dirty[index]))
                {
                    _dirty.RemoveAt(index);
                }
                else
                {
                    allWritten = false;
                    index++;
                }
            }

            return allWritten;
        }

        private Store Open(string name, string scope)
        {
            EnsureLoaded();
            string fileId = _persistence.FileIdFor(name, scope);
            if (_stores.TryGetValue(fileId, out Store store))
            {
                return store;
            }

            store = new Store(name, scope, fileId);
            _stores.Add(fileId, store);
            if (_corruptFiles.TryGetValue(fileId, out string unreadable))
            {
                _corruptFiles.Remove(fileId);
                throw RbxDataStoreErrors.Corrupted(name, unreadable);
            }

            return store;
        }

        private void RequireWritable()
        {
            if (WriteFailure.Length > 0)
            {
                throw RbxDataStoreErrors.Rejected("the game's storage refused the last write (" + WriteFailure + ")");
            }
        }

        private void MarkDirty(Store store)
        {
            if (store.Dirty)
            {
                return;
            }

            store.Dirty = true;
            store.DirtySince = _clock();
            _dirty.Add(store);
        }

        private bool Save(Store store)
        {
            try
            {
                if (store.Entries.Count == 0)
                {
                    _persistence.Delete(_namespace, store.Name, store.Scope);
                }
                else
                {
                    RbxDataStoreFile file = new() { Name = store.Name, Scope = store.Scope };
                    for (int index = 0; index < store.Order.Count; index++)
                    {
                        file.Entries.Add(store.Entries[store.Order[index]]);
                    }

                    _persistence.Save(_namespace, file);
                }

                store.Dirty = false;
                if (WriteFailure.Length > 0)
                {
                    _report?.Invoke("[DataStoreService] storage works again; writes are accepted.");
                }

                WriteFailure = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                bool first = WriteFailure.Length == 0;
                WriteFailure = Describe(ex);
                _retryAt = _clock() + _options.RetryDelaySeconds;
                if (first)
                {
                    _report?.Invoke("[DataStoreService] writing data store '" + store.Name + "' failed; it stays in "
                                    + "memory, is tried again, and new writes are refused until storage works: "
                                    + WriteFailure);
                }

                return false;
            }
        }

        private static string Describe(Exception ex)
        {
            return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        }
    }
}
