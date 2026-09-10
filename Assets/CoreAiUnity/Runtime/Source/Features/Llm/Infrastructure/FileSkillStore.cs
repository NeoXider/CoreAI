using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using CoreAI.Ai;
using CoreAI.Infrastructure;
using CoreAI.Logging;
using Newtonsoft.Json;
using UnityEngine;
using Cysharp.Threading.Tasks;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Atomic skill storage. All instances serialize writes and publication through the same canonical
    /// directory gate, including discovery and migration of legacy filenames. Existing unreadable records
    /// are never treated as missing during mutation. Logical ids are case-insensitive on every platform.
    /// On WebGL a successful write means the engine's automatic <c>persistentDataPath</c> persistence has
    /// taken it; no IndexedDB completion receipt exists to wait for (<see cref="CoreAiWebGlPersistence"/>).
    /// <para>
    /// <b>What "serialize" means for a synchronous caller</b> (see <see cref="Enter"/>): it waits out
    /// another SYNCHRONOUS operation and then runs - two writers both succeed, one after the other, and
    /// neither loses its edit. It is refused at once, with "busy; retry", while an ASYNCHRONOUS operation
    /// holds the gate, because that one releases from a continuation which may need the very thread the
    /// wait would park.
    /// </para>
    /// </summary>
    public sealed class FileSkillStore : ISkillStore, ICommittedSkillStore, IAsyncSkillStore, IDisposable
    {
        private static readonly JsonSerializerSettings JsonSettings = new() { Formatting = Formatting.Indented };
        private static readonly StringComparer PathComparer = Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private static readonly StringComparison PathComparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // WHY: never evict a live path gate; a waiter may still hold its instance.
        private static readonly ConcurrentDictionary<string, DirectoryState> MutationLocks = new(PathComparer);
        /// <summary>Maximum encoded size of one persisted skill document; oversized input fails without truncation.</summary>
        public const int MaxRecordBytes = 1024 * 1024;
        private sealed class DirectoryState
        {
            internal readonly SemaphoreSlim Gate = new(1, 1);
            internal Func<Task> PendingConfirmation;
            internal long Generation;
            /// <summary>
            /// Asynchronous operations on this directory that are queued for <see cref="Gate"/> or
            /// already holding it. Read by <see cref="FileSkillStore.Enter"/> to tell the two kinds of
            /// holder apart; a synchronous caller may wait for a synchronous one and never for one of
            /// these. Written with <see cref="Interlocked"/> only.
            /// </summary>
            internal int AsyncUsers;
        }
        private static readonly AsyncLocal<DirectoryState> Confirming = new();
        private readonly DirectoryState _state;
        private readonly ILlmAsyncMarshaler _host;
        private readonly Func<CancellationToken, Task<bool>> _confirm;
        private readonly bool _customConfirmation;
        private readonly string _dir;
        private readonly ILog _log;
        private readonly SemaphoreSlim _gate;
        private bool _disposed;

        public FileSkillStore(string rootDirectory = null, ILog log = null,
            ILlmAsyncMarshaler host = null, Func<CancellationToken, Task<bool>> confirmDurabilityAsync = null)
        {
            _dir = Path.GetFullPath(!string.IsNullOrWhiteSpace(rootDirectory)
                ? rootDirectory.Trim()
                : Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                    CoreAiPersistentPaths.Skills));
            if (_dir.Length > Path.GetPathRoot(_dir).Length)
                _dir = _dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _state = MutationLocks.GetOrAdd(_dir, _ => new DirectoryState());
            _gate = _state.Gate;
            _log = log;
            _host = host ?? UnityMainThreadLlmAsyncMarshaler.Instance;
            _customConfirmation = confirmDurabilityAsync != null;
            _confirm = confirmDurabilityAsync ?? (token => CoreAiWebGlPersistence.SyncAsync(token).AsTask());
        }

        public void Save(SkillRecord record)
        {
            ThrowIfDisposed();
            if (record == null || string.IsNullOrWhiteSpace(record.Id)) return;
            Mutate(record.Id, _ => SkillStoreMutation<bool>.SaveRecord(record, true));
        }

        public void Delete(string id)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(id)) return;
            Mutate(id, current => current == null
                ? SkillStoreMutation<bool>.NoChange(false)
                : SkillStoreMutation<bool>.DeleteRecord(true));
        }

        public TResult Mutate<TResult>(string id, Func<SkillRecord, SkillStoreMutation<TResult>> mutator)
        {
            return MutateAndPublish(id, mutator, null);
        }

        public TResult MutateAndPublish<TResult>(string id, Func<SkillRecord, SkillStoreMutation<TResult>> mutator,
            Action<TResult> publish)
        {
            ThrowIfDisposed();
            if (mutator == null) throw new ArgumentNullException(nameof(mutator));
            string skillId = Normalize(id);
            if (skillId.Length == 0) throw new ArgumentException("Skill id must not be empty.", nameof(id));
            _ = GetSkillPath(skillId);
            Enter(_gate);
            try
            {
                RequireConfirmed();
                SkillRecord current = FindRecord(skillId, out string existingPath);
                string stableId = current?.Id ?? skillId;
                string path = GetSkillPath(stableId);
                _ = ReadRecordStrict(path, stableId);
                if (current != null) MigrateRecord(existingPath, path);
                SkillStoreMutation<TResult> mutation = SkillStoreCallbackContext.Run(() => mutator(current))
                    ?? throw new InvalidOperationException("Skill store mutator returned null.");
                if (mutation.Delete)
                {
                    if (current != null)
                    {
                        File.Delete(path);
                        QueueDurability();
                    }
                }
                else if (mutation.Save)
                {
                    if (mutation.Record == null) throw new InvalidOperationException("Save mutation requires a record.");
                    if (!string.Equals(Normalize(mutation.Record.Id), skillId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A skill mutation cannot write a different key.");
                    _ = ReadRecordStrict(path, stableId);
                    SaveCore(stableId, mutation.Record, path);
                }
                // WHY: a later commit must not publish ahead of this one; callback stays inside the key gate.
                SkillStoreCallbackContext.Publish(skillId, mutation.Result, publish);
                return mutation.Result;
            }
            catch (Exception ex)
            {
                string stage = ex is SkillStorePublicationException ? "Publication failed after commit" : "Mutation failed";
                _log?.Error($"[FileSkillStore] {stage} for {skillId}: {ex}");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        private void SaveCore(string stableId, SkillRecord record, string path)
        {
            SkillRecord snapshot = new(stableId, record.Description, record.Instructions, record.ToolNames,
                record.Version, record.Sections);
            if (snapshot.Version < 0) throw new InvalidDataException("Skill version must not be negative.");
            Directory.CreateDirectory(_dir);
            AtomicWriteAllText(path, JsonConvert.SerializeObject(snapshot, JsonSettings));
            QueueDurability();
        }

        public bool TryLoad(string id, out SkillRecord record)
        {
            ThrowIfDisposed();
            record = null;
            string skillId = Normalize(id);
            if (skillId.Length == 0) return false;
            _ = GetSkillPath(skillId);
            Enter(_gate);
            try
            {
                RequireConfirmed();
                record = FindRecord(skillId, out string existingPath);
                if (record != null)
                {
                    string path = GetSkillPath(record.Id);
                    _ = ReadRecordStrict(path, record.Id);
                    MigrateRecord(existingPath, path);
                    RequireConfirmed();
                }
                return record != null;
            }
            catch (Exception ex) when (!(ex is InvalidOperationException))
            {
                record = null;
                _log?.Error($"[FileSkillStore] Load failed for {skillId}: {ex}");
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        public IReadOnlyList<SkillRecord> List()
        {
            ThrowIfDisposed();
            Enter(_gate);
            try
            {
                RequireConfirmed();
                Dictionary<string, SkillRecord> unique = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> ambiguous = new(StringComparer.OrdinalIgnoreCase);
                foreach (string path in GetRecordPaths())
                {
                    try
                    {
                        SkillRecord record = ReadRecordStrict(path, null);
                        if (record == null) continue;
                        ValidateRecordPath(path, record.Id);
                        string key = Normalize(record.Id);
                        if (unique.ContainsKey(key)) ambiguous.Add(key);
                        else unique.Add(key, record);
                    }
                    catch (Exception ex)
                    {
                        _log?.Error($"[FileSkillStore] Record read failed for {path}: {ex}");
                    }
                }
                foreach (string key in ambiguous)
                {
                    unique.Remove(key);
                    _log?.Error($"[FileSkillStore] Ambiguous skill '{key}': preserve files and resolve the duplicate before writing.");
                }
                List<SkillRecord> records = new(unique.Values);
                records.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id));
                return records.AsReadOnly();
            }
            finally { _gate.Release(); }
        }

        private string[] GetRecordPaths()
        {
            try { return Directory.GetFiles(_dir, "*.json"); }
            catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
        }

        private SkillRecord FindRecord(string id, out string existingPath)
        {
            SkillRecord found = null;
            existingPath = null;
            // WHY: legacy filenames encode the old case and platform-specific sanitization. Discover by
            // validated stored identity before migration; an unreadable record may be this same skill.
            foreach (string path in GetRecordPaths())
            {
                SkillRecord record = ReadRecordStrict(path, null);
                if (record == null) continue;
                ValidateRecordPath(path, record.Id);
                if (!string.Equals(Normalize(record.Id), id, StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null)
                    throw new InvalidDataException($"Ambiguous skill '{id}': multiple records exist; all files preserved.");
                found = record;
                existingPath = path;
            }
            return found;
        }

        private void ValidateRecordPath(string path, string id)
        {
            string filename = Path.GetFileName(path);
            if (PathComparer.Equals(GetSkillPath(id), Path.GetFullPath(path)) ||
                string.Equals(filename, HashedFileName(id, true) + ".json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(filename, LegacyFileName(id, true) + ".json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(filename, LegacyFileName(id, false) + ".json", StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidDataException("Existing skill filename does not match its identity; preserved without changes.");
        }

        private void MigrateRecord(string existingPath, string canonicalPath)
        {
            if (PathComparer.Equals(Path.GetFullPath(existingPath), canonicalPath)) return;
            // WHY: one same-directory rename preserves the complete old bytes if the later content write fails.
            // File.Move refuses an occupied destination; migration never chooses a winning duplicate.
            File.Move(existingPath, canonicalPath);
            QueueDurability();
        }
        private static SkillRecord ReadRecordStrict(string path, string expectedId)
        {
            string json;
            try
            {
                if (new FileInfo(path).Length > MaxRecordBytes)
                    throw new InvalidDataException($"Skill record exceeds the {MaxRecordBytes}-byte limit.");
                json = File.ReadAllText(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            SkillRecord record;
            try
            {
                record = JsonConvert.DeserializeObject<SkillRecord>(json, JsonSettings);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Existing skill record is not readable JSON; preserved without changes.", ex);
            }
            if (record == null || string.IsNullOrWhiteSpace(record.Id) || record.Version < 0 ||
                (expectedId != null && !string.Equals(Normalize(record.Id), expectedId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Existing skill record has invalid identity or version; preserved without changes.");
            }
            return record;
        }

        private string GetSkillPath(string id)
        {
            string path = Path.GetFullPath(Path.Combine(_dir, CanonicalFileName(id) + ".json"));
            string prefix = _dir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? _dir : _dir + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, PathComparison)) throw new InvalidOperationException("Skill path escapes its store root.");
            return path;
        }

        private static string CanonicalFileName(string id)
        {
            return HashedFileName(id, false);
        }

        private static string HashedFileName(string id, bool legacyCaseFold)
        {
            using SHA256 hash = SHA256.Create();
            // WHY: replacement fallback maps different invalid surrogate ids onto the same file key.
            string source = Normalize(id);
            if (legacyCaseFold) source = source.ToUpperInvariant();
            byte[] bytes = hash.ComputeHash(new UTF8Encoding(false, true).GetBytes(source));
            // WHY: ToUpperInvariant is not OrdinalIgnoreCase identity (for example, long-s and ASCII S).
            // Preserve the original stored id; aliases resolve it through FindRecord instead of renaming it.
            StringBuilder name = new(legacyCaseFold ? "skill-" : "skill-v2-");
            foreach (byte value in bytes) name.Append(value.ToString("x2"));
            return name.ToString();
        }

        private static string LegacyFileName(string id, bool windows)
        {
            StringBuilder safe = new();
            bool changed = false;
            foreach (char c in id)
            {
                bool invalid = c == '\0' || c == '/' || (windows && (c < 32 || "<>:\"\\|?*".IndexOf(c) >= 0));
                safe.Append(invalid ? '_' : c);
                changed |= invalid;
            }
            if (!changed) return safe.ToString();
            uint hash = 2166136261u;
            foreach (char c in id) hash = unchecked((hash ^ c) * 16777619u);
            return $"{safe}_{hash:x8}";
        }
        private void AtomicWriteAllText(string path, string contents, CancellationToken cancellationToken = default)
        {
            if (Encoding.UTF8.GetByteCount(contents) > MaxRecordBytes)
                throw new InvalidDataException($"Skill record exceeds the {MaxRecordBytes}-byte limit.");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, contents);
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException || failure is System.Security.SecurityException) { }
            }
        }

        /// <summary>
        /// Takes the directory gate for a SYNCHRONOUS operation. The free gate is the common case and
        /// costs one non-blocking probe; the two busy cases are deliberately different.
        /// <para>
        /// The platform fork arrived with these tests in 7.36.0 and was lost in the publication wave of
        /// 7.39.0, which left the WebGL half running everywhere. On a desktop or editor thread that
        /// turned "wait your turn" into "Skill store is busy" for a caller that had no way to know a
        /// turn was even needed: two mods (or two coordinators over one folder) saving a skill in the
        /// same moment produced one saved skill and one thrown exception, and the synchronous API -
        /// which is what SkillAuthoringCoordinator, AgentBuilder and Save/Delete/Mutate all use - has no
        /// retry of its own, so the second edit was simply lost. It now waits and then runs, as before.
        /// </para>
        /// </summary>
        private void Enter(SemaphoreSlim gate)
        {
            SkillStoreCallbackContext.ThrowIfActive();
            ThrowIfConfirming();
            if (gate.Wait(0)) return;
#if UNITY_WEBGL && !UNITY_EDITOR
            // A WebGL player has ONE thread. Parking it parks the code that would release the gate, so
            // the wait could never end: a busy store answers immediately and the caller retries.
            throw new InvalidOperationException("Skill store is busy; retry after the current operation.");
#else
            // An ASYNCHRONOUS holder releases the gate from a continuation, and that continuation may be
            // owed to the very thread a wait here would park (an await in this file captures the caller's
            // SynchronizationContext). That is refused, not awaited - it is the deadlock
            // FileAgentMemoryStore still carries, and FileSkillStoreAsyncEditModeTests pins the refusal
            // ("WithoutBlockingSyncCaller"). A SYNCHRONOUS holder releases on its own thread, so waiting
            // it out is safe and is the whole point of a store that serializes.
            if (Volatile.Read(ref _state.AsyncUsers) > 0)
                throw new InvalidOperationException("Skill store is busy; retry after the current operation.");
            gate.Wait();
#endif
        }

        /// <summary>
        /// Takes the directory gate for an ASYNCHRONOUS operation and marks it in flight for as long as
        /// it is queued or holding, so <see cref="Enter"/> can refuse to park a thread behind it. The
        /// mark is raised before the wait on purpose: a queued async operation can win the gate the
        /// instant it is released, and a synchronous caller must not be parked behind that either.
        /// </summary>
        private async Task<AsyncLease> EnterAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _state.AsyncUsers);
            try
            {
                await _gate.WaitAsync(cancellationToken);
            }
            catch
            {
                Interlocked.Decrement(ref _state.AsyncUsers);
                throw;
            }

            return new AsyncLease(this);
        }

        private readonly struct AsyncLease : IDisposable
        {
            private readonly FileSkillStore _store;
            internal AsyncLease(FileSkillStore store) => _store = store;

            public void Dispose()
            {
                _store._gate.Release();
                Interlocked.Decrement(ref _store._state.AsyncUsers);
            }
        }

        private void ThrowIfConfirming()
        {
            if (Confirming.Value != null)
                throw new InvalidOperationException("A skill durability callback cannot reenter skill storage.");
        }

        private void RequireConfirmed()
        {
            if (_state.PendingConfirmation != null)
                throw new InvalidOperationException("Skill durability is unconfirmed; await an async read before using stored records.");
        }

        private void QueueDurability()
        {
            // WHY: Only a caller-supplied confirmation hook is asynchronous, so only it can leave an
            // unresolved confirmation behind. Durability on WebGL is answered synchronously by the
            // engine (CoreAiWebGlPersistence.Sync), so this path must NOT park a pending confirmation
            // of its own: it used to do that unconditionally under "#if UNITY_WEBGL && !UNITY_EDITOR",
            // and every production caller reaching here is synchronous (SkillAuthoringCoordinator,
            // AgentBuilder, Save/Delete/Mutate) - nothing ever cleared it. The first skill written in
            // the browser poisoned the store, and every later synchronous call threw "Skill durability
            // is unconfirmed". Reading poisoned it too: MigrateRecord runs from TryLoad, so a single
            // skill file left under a legacy name turned a plain read into an exception the read's own
            // catch deliberately lets through.
            // The same correction is in FileLuaScriptVersionStore.Mutate; the two must stay in step.
            if (_customConfirmation) RecordPending(_host);
            if (!CoreAiWebGlPersistence.Sync()) throw new SkillStoreDurabilityException("store");
        }

        private void RecordPending(ILlmAsyncMarshaler host)
        {
            _state.Generation++;
            _state.PendingConfirmation = async () =>
            {
                await host.InvokeAsync(async () =>
                {
                    DirectoryState previous = Confirming.Value;
                    Confirming.Value = _state;
                    try
                    {
                        if (!await _confirm(CancellationToken.None)) throw new IOException("Skill durability confirmation failed.");
                        return true;
                    }
                    finally { Confirming.Value = previous; }
                }, CancellationToken.None);
            };
        }

        private async Task ConfirmPendingAsync(string id)
        {
            Func<Task> pending = _state.PendingConfirmation;
            if (pending == null) return;
            long generation = _state.Generation;
            try { await pending(); }
            catch (Exception ex) { throw new SkillStoreDurabilityException(id, ex); }
            if (_state.Generation == generation) _state.PendingConfirmation = null;
        }

        private Task<T> FileWorkAsync<T>(Func<T> work, CancellationToken token)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return _host.InvokeAsync(() => { token.ThrowIfCancellationRequested(); return Task.FromResult(work()); }, token);
#else
            return Task.Run(() => { token.ThrowIfCancellationRequested(); return work(); }, token);
#endif
        }

        private async Task<List<(SkillRecord Record, string Path)>> ReadRecordsAsync(CancellationToken token)
        {
            string[] paths = await FileWorkAsync(GetRecordPaths, token);
            List<(SkillRecord Record, string Path)> records = new(paths.Length);
            HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                SkillRecord record = await FileWorkAsync(() =>
                {
                    SkillRecord loaded = ReadRecordStrict(path, null);
                    if (loaded != null) ValidateRecordPath(path, loaded.Id);
                    return loaded;
                }, token);
                if (record == null) continue;
                if (!ids.Add(Normalize(record.Id))) throw new InvalidDataException("Ambiguous skill identity; all files preserved.");
                records.Add((record, path));
#if UNITY_WEBGL && !UNITY_EDITOR
                await _host.DelayAsync(1, token);
#endif
            }
            return records;
        }

        /// <inheritdoc />
        public async Task<SkillRecord> LoadAsync(string id, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            SkillStoreCallbackContext.ThrowIfActive();
            ThrowIfConfirming();
            string key = Normalize(id);
            if (key.Length == 0) return null;
            using (await EnterAsync(cancellationToken))
            {
                await ConfirmPendingAsync(key);
                List<(SkillRecord Record, string Path)> records = await ReadRecordsAsync(cancellationToken);
                foreach ((SkillRecord record, string path) in records)
                {
                    if (!string.Equals(Normalize(record.Id), key, StringComparison.OrdinalIgnoreCase)) continue;
                    string canonical = await FileWorkAsync(() => GetSkillPath(record.Id), cancellationToken);
                    if (!PathComparer.Equals(Path.GetFullPath(path), canonical))
                    {
                        await FileWorkAsync(() => { cancellationToken.ThrowIfCancellationRequested(); File.Move(path, canonical); return true; }, cancellationToken);
                        RecordPending(_host);
                        await ConfirmPendingAsync(key);
                    }
                    return record;
                }
                return null;
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<SkillRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            SkillStoreCallbackContext.ThrowIfActive();
            ThrowIfConfirming();
            using (await EnterAsync(cancellationToken))
            {
                await ConfirmPendingAsync("store");
                List<(SkillRecord Record, string Path)> entries = await ReadRecordsAsync(cancellationToken);
                List<SkillRecord> records = new(entries.Count);
                foreach ((SkillRecord record, string path) in entries) records.Add(record);
                records.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id));
                return records.AsReadOnly();
            }
        }

        /// <inheritdoc />
        public async Task<TResult> MutateAndPublishAsync<TResult>(string id,
            Func<SkillRecord, SkillStoreMutation<TResult>> prepare,
            Func<TResult, CancellationToken, Task> publish, ILlmAsyncMarshaler callbackContext,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            SkillStoreCallbackContext.ThrowIfActive();
            ThrowIfConfirming();
            if (prepare == null) throw new ArgumentNullException(nameof(prepare));
            if (callbackContext == null) throw new ArgumentNullException(nameof(callbackContext));
            string key = Normalize(id);
            if (key.Length == 0) throw new ArgumentException("Skill id must not be empty.", nameof(id));
            using (await EnterAsync(cancellationToken))
            {
                await ConfirmPendingAsync(key);
                List<(SkillRecord Record, string Path)> entries = await ReadRecordsAsync(cancellationToken);
                SkillRecord current = null;
                string existingPath = null;
                foreach ((SkillRecord record, string path) in entries)
                    if (string.Equals(Normalize(record.Id), key, StringComparison.OrdinalIgnoreCase)) { current = record; existingPath = path; }
                string stableId = current?.Id ?? key;
                // An existing legacy file is replaced atomically in place; read-time migration can happen later.
                string target = existingPath ?? await FileWorkAsync(() => GetSkillPath(stableId), cancellationToken);
                SkillStoreMutation<TResult> mutation = await callbackContext.InvokeAsync(
                    () => Task.FromResult(SkillStoreCallbackContext.Run(() => prepare(current))), cancellationToken)
                    ?? throw new InvalidOperationException("Skill store mutator returned null.");
                string json = null;
                if (mutation.Save)
                {
                    if (mutation.Record == null || !string.Equals(Normalize(mutation.Record.Id), key, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A skill mutation cannot write a different key.");
                    SkillRecord snapshot = new(stableId, mutation.Record.Description, mutation.Record.Instructions,
                        mutation.Record.ToolNames, mutation.Record.Version, mutation.Record.Sections);
                    if (snapshot.Version < 0) throw new InvalidDataException("Skill version must not be negative.");
                    json = await FileWorkAsync(() => JsonConvert.SerializeObject(snapshot, JsonSettings), cancellationToken);
                }
                bool changed = mutation.Save || (mutation.Delete && current != null);
                cancellationToken.ThrowIfCancellationRequested();
                if (changed)
                {
                    await FileWorkAsync(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (mutation.Delete) File.Delete(target);
                        else { Directory.CreateDirectory(_dir); AtomicWriteAllText(target, json, cancellationToken); }
                        return true;
                    }, cancellationToken);
                    RecordPending(callbackContext);
                    await ConfirmPendingAsync(key);
                }
                await SkillStoreCallbackContext.PublishAsync(key, mutation.Result, publish, callbackContext);
                return mutation.Result;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FileSkillStore));
        }

        public void Dispose() => _disposed = true;
        private static string Normalize(string value) => (value ?? "").Trim();
    }
}
