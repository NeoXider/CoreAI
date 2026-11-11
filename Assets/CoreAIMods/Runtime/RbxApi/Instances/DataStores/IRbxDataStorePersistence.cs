using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.DataStores
{
    /// <summary>One key of a data store as it is kept: the value as JSON text plus the key's information.</summary>
    public sealed class RbxDataStoreEntry
    {
        /// <summary>The key.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>The value, encoded by the shared table/JSON contract.</summary>
        public string ValueJson { get; set; } = "null";

        /// <summary><c>DataStoreKeyInfo.CreatedTime</c>: milliseconds since the Unix epoch.</summary>
        public long CreatedMilliseconds { get; set; }

        /// <summary><c>DataStoreKeyInfo.UpdatedTime</c>: milliseconds since the Unix epoch.</summary>
        public long UpdatedMilliseconds { get; set; }

        /// <summary>The number behind <c>DataStoreKeyInfo.Version</c>; one more on every write.</summary>
        public long Version { get; set; }

        /// <summary>The user ids the last write tagged the key with.</summary>
        public long[] UserIds { get; set; } = Array.Empty<long>();

        /// <summary>The user metadata the last write set, as JSON text ("{}" when none).</summary>
        public string MetadataJson { get; set; } = "{}";

        /// <summary>The characters this entry counts against a world's storage limit.</summary>
        public long StoredCharacters => (Key?.Length ?? 0) + (ValueJson?.Length ?? 0) + (MetadataJson?.Length ?? 0)
                                        + 16L * ((UserIds?.Length ?? 0) + 4L);

        /// <summary>A copy that a later write cannot change.</summary>
        public RbxDataStoreEntry Copy()
        {
            return new RbxDataStoreEntry
            {
                Key = Key,
                ValueJson = ValueJson,
                CreatedMilliseconds = CreatedMilliseconds,
                UpdatedMilliseconds = UpdatedMilliseconds,
                Version = Version,
                UserIds = UserIds == null ? Array.Empty<long>() : (long[])UserIds.Clone(),
                MetadataJson = MetadataJson
            };
        }
    }

    /// <summary>One data store (a name and a scope) with all its keys, as one unit of storage.</summary>
    public sealed class RbxDataStoreFile
    {
        /// <summary>The data store name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The scope.</summary>
        public string Scope { get; set; } = RbxDataStoreClasses.DefaultScope;

        /// <summary>The keys, in the order they were first written.</summary>
        public List<RbxDataStoreEntry> Entries { get; } = new();
    }

    /// <summary>What reading one stored data store gave.</summary>
    public enum RbxDataStoreLoadStatus
    {
        /// <summary>The file was read.</summary>
        Loaded = 0,

        /// <summary>The file could not be read; it was set aside and the store starts empty.</summary>
        Corrupt = 1
    }

    /// <summary>One data store of a namespace as storage gave it back.</summary>
    public sealed class RbxDataStoreLoadResult
    {
        /// <summary>Creates a result.</summary>
        public RbxDataStoreLoadResult(string fileId, RbxDataStoreLoadStatus status, RbxDataStoreFile file, string problem)
        {
            FileId = fileId ?? string.Empty;
            Status = status;
            File = file;
            Problem = problem ?? string.Empty;
        }

        /// <summary>The storage id of the store (<see cref="IRbxDataStorePersistence.FileIdFor"/>).</summary>
        public string FileId { get; }

        /// <summary>Whether the file was read.</summary>
        public RbxDataStoreLoadStatus Status { get; }

        /// <summary>The store, or null when it was unreadable.</summary>
        public RbxDataStoreFile File { get; }

        /// <summary>Why an unreadable file was unreadable and where it was set aside; empty when it was read.</summary>
        public string Problem { get; }
    }

    /// <summary>
    /// The engine-free storage port of <c>DataStoreService</c>: a namespace (one game's or one world's data) holds
    /// data stores, each kept and replaced as a whole. <see cref="InMemoryRbxDataStorePersistence"/> serves tests and
    /// headless hosts; the game's file implementation lives in the Mods infrastructure. A host adapts its own backend
    /// (a save service, a database) by implementing this.
    /// </summary>
    /// <remarks>
    /// Calls come from the main thread, never in parallel. A failure throws; the caller keeps the data in memory,
    /// reports the failure to scripts and tries again later.
    /// </remarks>
    public interface IRbxDataStorePersistence
    {
        /// <summary>A stable, storage-safe id of the store <paramref name="name"/> with <paramref name="scope"/>.</summary>
        string FileIdFor(string name, string scope);

        /// <summary>Every data store kept in <paramref name="dataNamespace"/>; an unreadable one is reported, set aside and not returned as data.</summary>
        IReadOnlyList<RbxDataStoreLoadResult> LoadNamespace(string dataNamespace);

        /// <summary>Keeps <paramref name="file"/> in <paramref name="dataNamespace"/>, replacing what was kept for it.</summary>
        void Save(string dataNamespace, RbxDataStoreFile file);

        /// <summary>Forgets the store <paramref name="name"/> with <paramref name="scope"/>; nothing happens when it is not kept.</summary>
        void Delete(string dataNamespace, string name, string scope);
    }

    /// <summary>
    /// Storage that lives as long as the object: for tests and headless hosts. Saved stores are copied in and out,
    /// so a test that keeps the instance and builds a new backend over it sees what a restart would read.
    /// </summary>
    public sealed class InMemoryRbxDataStorePersistence : IRbxDataStorePersistence
    {
        private readonly Dictionary<string, Dictionary<string, RbxDataStoreFile>> _namespaces = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _unreadable = new(StringComparer.Ordinal);

        /// <summary>How many saves were made, for tests that check coalescing.</summary>
        public int SaveCount { get; private set; }

        /// <summary>When set, every save throws this message (a disk that refuses writes).</summary>
        public string FailSavesWith { get; set; }

        /// <inheritdoc />
        public string FileIdFor(string name, string scope)
        {
            return (name ?? string.Empty) + "\n" + (scope ?? string.Empty);
        }

        /// <inheritdoc />
        public IReadOnlyList<RbxDataStoreLoadResult> LoadNamespace(string dataNamespace)
        {
            List<RbxDataStoreLoadResult> results = new();
            string key = dataNamespace ?? string.Empty;
            if (_namespaces.TryGetValue(key, out Dictionary<string, RbxDataStoreFile> files))
            {
                foreach (KeyValuePair<string, RbxDataStoreFile> pair in files)
                {
                    results.Add(new RbxDataStoreLoadResult(pair.Key, RbxDataStoreLoadStatus.Loaded, CopyFile(pair.Value), null));
                }
            }

            if (_unreadable.TryGetValue(key, out Dictionary<string, string> broken))
            {
                foreach (KeyValuePair<string, string> pair in broken)
                {
                    results.Add(new RbxDataStoreLoadResult(pair.Key, RbxDataStoreLoadStatus.Corrupt, null, pair.Value));
                }

                broken.Clear();
            }

            return results;
        }

        /// <inheritdoc />
        public void Save(string dataNamespace, RbxDataStoreFile file)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            if (!string.IsNullOrEmpty(FailSavesWith))
            {
                throw new InvalidOperationException(FailSavesWith);
            }

            SaveCount++;
            string key = dataNamespace ?? string.Empty;
            if (!_namespaces.TryGetValue(key, out Dictionary<string, RbxDataStoreFile> files))
            {
                files = new Dictionary<string, RbxDataStoreFile>(StringComparer.Ordinal);
                _namespaces.Add(key, files);
            }

            files[FileIdFor(file.Name, file.Scope)] = CopyFile(file);
        }

        /// <inheritdoc />
        public void Delete(string dataNamespace, string name, string scope)
        {
            if (_namespaces.TryGetValue(dataNamespace ?? string.Empty, out Dictionary<string, RbxDataStoreFile> files))
            {
                files.Remove(FileIdFor(name, scope));
            }
        }

        /// <summary>Marks the store <paramref name="name"/> with <paramref name="scope"/> unreadable, as a corrupted file would be.</summary>
        public void Corrupt(string dataNamespace, string name, string scope, string problem)
        {
            string key = dataNamespace ?? string.Empty;
            if (_namespaces.TryGetValue(key, out Dictionary<string, RbxDataStoreFile> files))
            {
                files.Remove(FileIdFor(name, scope));
            }

            if (!_unreadable.TryGetValue(key, out Dictionary<string, string> broken))
            {
                broken = new Dictionary<string, string>(StringComparer.Ordinal);
                _unreadable.Add(key, broken);
            }

            broken[FileIdFor(name, scope)] = problem ?? "corrupted";
        }

        /// <summary>The kept copy of a store, or null.</summary>
        public RbxDataStoreFile Peek(string dataNamespace, string name, string scope)
        {
            return _namespaces.TryGetValue(dataNamespace ?? string.Empty, out Dictionary<string, RbxDataStoreFile> files)
                   && files.TryGetValue(FileIdFor(name, scope), out RbxDataStoreFile file)
                ? CopyFile(file)
                : null;
        }

        private static RbxDataStoreFile CopyFile(RbxDataStoreFile source)
        {
            RbxDataStoreFile copy = new() { Name = source.Name, Scope = source.Scope };
            for (int index = 0; index < source.Entries.Count; index++)
            {
                copy.Entries.Add(source.Entries[index].Copy());
            }

            return copy;
        }
    }
}
