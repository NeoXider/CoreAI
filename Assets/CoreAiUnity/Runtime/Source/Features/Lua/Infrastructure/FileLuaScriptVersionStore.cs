using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Atomic version persistence with lazy loading and staged snapshots. Private file/JSON work runs
    /// on a worker on desktop and on the host in WebGL. The canonical-path gate remains held through
    /// durability confirmation and snapshot publication. Failed flushes remain pending across instances.
    /// </summary>
    public sealed class FileLuaScriptVersionStore : ILuaScriptVersionStore, IAsyncLuaScriptVersionStore
    {
        /// <summary>Maximum encoded file size; larger files fail explicitly without truncation.</summary>
        public const int MaxStoreBytes = 4 * 1024 * 1024;
        private sealed class PathState
        {
            internal readonly SemaphoreSlim Gate = new(1, 1);
            internal Func<Task> PendingConfirmation;
            internal long Generation;
        }
        private static readonly ConcurrentDictionary<string, PathState> FileLocks = new(
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private static readonly AsyncLocal<PathState> Confirming = new();
        private static readonly JsonSerializerSettings JsonSettings = new() { Formatting = Formatting.Indented };
        private readonly IGameLogger _logger;
        private readonly string _filePath;
        private readonly PathState _state;
        private readonly ILlmAsyncMarshaler _host;
        private readonly Func<CancellationToken, Task<bool>> _confirm;
        private readonly bool _customConfirmation;
        private readonly int _maxIntermediateRevisions;
        private readonly long _maxTotalBytes;
        internal static Action BeforeAtomicReplaceForTesting;

        /// <summary>
        /// Synchronous durability answer for the synchronous mutation path. Defaults to
        /// <see cref="CoreAiWebGlPersistence.Sync"/>, which is true everywhere except a WebGL page that
        /// never armed automatic persistence. Internal setter is the test seam: the refusal branch is
        /// otherwise unreachable outside a misconfigured browser.
        /// </summary>
        internal Func<bool> FlushDurabilityForTesting
        {
            get => _flushDurability;
            set => _flushDurability = value ?? CoreAiWebGlPersistence.Sync;
        }

        private Func<bool> _flushDurability = CoreAiWebGlPersistence.Sync;

        public FileLuaScriptVersionStore(IGameLogger logger,
            int maxIntermediateRevisions = VersionRetentionPolicy.DefaultMaxIntermediateRevisions,
            long maxTotalBytes = VersionRetentionPolicy.DefaultMaxTotalBytes)
            : this(logger, Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                CoreAiPersistentPaths.LuaScriptVersions, "lua_script_versions.json"), maxIntermediateRevisions, maxTotalBytes) { }

        /// <summary>Configures persistence without creating directories or reading files.</summary>
        public FileLuaScriptVersionStore(IGameLogger logger, string jsonFilePath,
            int maxIntermediateRevisions = VersionRetentionPolicy.DefaultMaxIntermediateRevisions,
            long maxTotalBytes = VersionRetentionPolicy.DefaultMaxTotalBytes,
            ILlmAsyncMarshaler host = null, Func<CancellationToken, Task<bool>> confirmDurabilityAsync = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _filePath = Path.GetFullPath(jsonFilePath ?? throw new ArgumentNullException(nameof(jsonFilePath)));
            _state = FileLocks.GetOrAdd(_filePath, _ => new PathState());
            _host = host ?? UnityMainThreadLlmAsyncMarshaler.Instance;
            _customConfirmation = confirmDurabilityAsync != null;
            _confirm = confirmDurabilityAsync ?? (token => CoreAiWebGlPersistence.SyncAsync(token).AsTask());
            _maxIntermediateRevisions = maxIntermediateRevisions;
            _maxTotalBytes = maxTotalBytes;
        }
        private MemoryLuaScriptVersionStore NewMemory() => new(_maxIntermediateRevisions, _maxTotalBytes);
        public bool TryGetSnapshot(string key, out LuaScriptVersionRecord snapshot)
        {
            snapshot = Read(memory => memory.TryGetSnapshot(key, out LuaScriptVersionRecord record) ? record : null);
            return snapshot != null;
        }
        public IReadOnlyList<string> GetKnownKeys() => Read(memory => memory.GetKnownKeys());
        public string BuildProgrammerPromptSection(string key) => Read(memory => memory.BuildProgrammerPromptSection(key));
        public void SeedOriginal(string key, string source, bool overwriteExistingOriginal = false)
            => Mutate(memory => memory.SeedOriginalChanged(key, source, overwriteExistingOriginal));
        public void RecordSuccessfulExecution(string key, string source) => Mutate(memory => memory.RecordSuccessfulExecutionChanged(key, source));
        public void ResetToOriginal(string key) => Mutate(memory => memory.ResetToOriginalChanged(key));
        public void ResetToRevision(string key, int revisionIndex) => Mutate(memory => memory.ResetToRevisionChanged(key, revisionIndex));
        public void ResetAllToOriginal() => Mutate(memory =>
        {
            bool changed = false;
            foreach (string key in memory.GetKnownKeys()) changed |= memory.ResetToOriginalChanged(key);
            return changed;
        });
        public Task<LuaScriptVersionRecord> GetSnapshotAsync(string key, CancellationToken cancellationToken = default)
            => ReadAsync(memory => memory.TryGetSnapshot(key, out LuaScriptVersionRecord record) ? record : null, cancellationToken);
        public Task<IReadOnlyList<string>> GetKnownKeysAsync(CancellationToken cancellationToken = default)
            => ReadAsync(memory => memory.GetKnownKeys(), cancellationToken);
        public Task SeedOriginalAsync(string key, string source, bool overwriteExistingOriginal = false, CancellationToken cancellationToken = default)
            => MutateAsync(memory => memory.SeedOriginalChanged(key, source, overwriteExistingOriginal), cancellationToken);
        public Task RecordSuccessfulExecutionAsync(string key, string source, CancellationToken cancellationToken = default)
            => MutateAsync(memory => memory.RecordSuccessfulExecutionChanged(key, source), cancellationToken);

        private void EnterSync()
        {
            ThrowIfConfirming();
            if (!_state.Gate.Wait(0)) throw new InvalidOperationException("Version store is busy; retry after the current operation.");
        }
        private void ThrowIfConfirming()
        {
            if (Confirming.Value != null) throw new InvalidOperationException("A version durability callback cannot reenter version storage.");
        }
        private void RequireConfirmed()
        {
            if (_state.PendingConfirmation != null)
                throw new InvalidOperationException("Version durability is unconfirmed; await an async read before using stored revisions.");
        }
        private T Read<T>(Func<MemoryLuaScriptVersionStore, T> read)
        {
            EnterSync();
            try { RequireConfirmed(); return read(LoadStaged()); }
            finally { _state.Gate.Release(); }
        }
        private void Mutate(Func<MemoryLuaScriptVersionStore, bool> mutate)
        {
            EnterSync();
            try
            {
                RequireConfirmed();
                MemoryLuaScriptVersionStore staged = LoadStaged();
                if (mutate(staged))
                {
                    WriteAtomically(Serialize(staged), CancellationToken.None);
                    // WHY: Only a caller-supplied confirmation hook is asynchronous, so only it can leave
                    // an unresolved confirmation behind. Durability on WebGL is now answered synchronously
                    // by the engine (CoreAiWebGlPersistence.Sync), so this path must NOT park a pending
                    // confirmation of its own: it used to do that unconditionally under
                    // "#if UNITY_WEBGL && !UNITY_EDITOR", and the production callers of this API
                    // (LuaCsModRuntime.RecordRevision, LuaCsVersioningRuntimeBindings, the demo
                    // controllers) are all synchronous, so nothing ever cleared it. The first mod that
                    // recorded a revision poisoned the store and every later synchronous call threw
                    // "Version durability is unconfirmed" - two errors per mod load in the browser.
                    if (_customConfirmation) RecordPending();
                    if (!_flushDurability()) throw new IOException("Version write committed locally, but the browser page has no durable storage armed.");
                }
            }
            catch (Exception ex) { _logger.LogError(GameLogFeature.Core, $"Version persistence failed ({ex.GetType().Name})."); throw; }
            finally { _state.Gate.Release(); }
        }
        private async Task<T> ReadAsync<T>(Func<MemoryLuaScriptVersionStore, T> read, CancellationToken token)
        {
            ThrowIfConfirming();
            await _state.Gate.WaitAsync(token);
            try
            {
                await ConfirmPendingAsync();
                MemoryLuaScriptVersionStore staged = await LoadStagedAsync(token);
                T result = await FileWorkAsync(() => read(staged), token);
                return result;
            }
            finally { _state.Gate.Release(); }
        }
        private async Task MutateAsync(Func<MemoryLuaScriptVersionStore, bool> mutate, CancellationToken token)
        {
            ThrowIfConfirming();
            await _state.Gate.WaitAsync(token);
            try
            {
                await ConfirmPendingAsync();
                MemoryLuaScriptVersionStore staged = await LoadStagedAsync(token);
                bool changed = await FileWorkAsync(() => mutate(staged), token);
                if (changed)
                {
                    string json = await FileWorkAsync(() => Serialize(staged), token);
                    await FileWorkAsync(() => { WriteAtomically(json, token); return true; }, token);
                    RecordPending();
                    await ConfirmPendingAsync();
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                await _host.InvokeAsync(() => { _logger.LogError(GameLogFeature.Core, $"Version persistence failed ({ex.GetType().Name})."); return Task.FromResult(true); }, CancellationToken.None);
                throw;
            }
            finally { _state.Gate.Release(); }
        }
        private void RecordPending()
        {
            _state.Generation++;
            _state.PendingConfirmation = async () =>
            {
                await _host.InvokeAsync(async () =>
                {
                    PathState previous = Confirming.Value;
                    Confirming.Value = _state;
                    try
                    {
                        if (!await _confirm(CancellationToken.None)) throw new IOException("Version durability confirmation failed.");
                        return true;
                    }
                    finally { Confirming.Value = previous; }
                }, CancellationToken.None);
            };
        }
        private async Task ConfirmPendingAsync()
        {
            Func<Task> pending = _state.PendingConfirmation;
            if (pending == null) return;
            long generation = _state.Generation;
            try { await pending(); }
            catch (Exception ex) { throw new IOException("Version storage is locally committed but durability remains unconfirmed; do not replay the edit.", ex); }
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
        private MemoryLuaScriptVersionStore LoadStaged() => Import(ReadDto());
        private async Task<MemoryLuaScriptVersionStore> LoadStagedAsync(CancellationToken token)
        {
            PersistRootDto dto = await FileWorkAsync(ReadDto, token);
#if UNITY_WEBGL && !UNITY_EDITOR
            List<LuaScriptVersionRecord> records = new(dto.slots.Count);
            foreach (PersistSlotDto slot in dto.slots)
            {
                records.Add(await FileWorkAsync(() => ToRecord(slot), token));
                await _host.DelayAsync(1, token);
            }
            return await FileWorkAsync(() => { MemoryLuaScriptVersionStore staged = NewMemory(); staged.ImportFromRecords(records); return staged; }, token);
#else
            return await FileWorkAsync(() => Import(dto), token);
#endif
        }
        private PersistRootDto ReadDto()
        {
            string json;
            try
            {
                if (new FileInfo(_filePath).Length > MaxStoreBytes) throw new InvalidDataException($"Version store exceeds the {MaxStoreBytes}-byte limit.");
                json = File.ReadAllText(_filePath);
            }
            catch (FileNotFoundException) { return new PersistRootDto(); }
            catch (DirectoryNotFoundException) { return new PersistRootDto(); }
            PersistRootDto dto;
            try { dto = JsonConvert.DeserializeObject<PersistRootDto>(json, JsonSettings); }
            catch (JsonException ex) { throw new InvalidDataException("Version store JSON is corrupt; existing data is preserved.", ex); }
            if (dto?.slots == null) throw new InvalidDataException("Version store has no valid slots collection.");
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (PersistSlotDto slot in dto.slots)
                if (slot == null || string.IsNullOrWhiteSpace(slot.scriptKey) || slot.history == null || !keys.Add(slot.scriptKey.Trim()))
                    throw new InvalidDataException("Version store contains invalid or duplicate slots.");
            return dto;
        }
        private MemoryLuaScriptVersionStore Import(PersistRootDto dto)
        {
            List<LuaScriptVersionRecord> records = new(dto.slots.Count);
            foreach (PersistSlotDto slot in dto.slots) records.Add(ToRecord(slot));
            MemoryLuaScriptVersionStore staged = NewMemory();
            staged.ImportFromRecords(records);
            return staged;
        }
        private static LuaScriptVersionRecord ToRecord(PersistSlotDto slot)
        {
            List<LuaScriptRevision> history = new(slot.history.Count);
            HashSet<int> indices = new();
            foreach (PersistRevDto revision in slot.history)
            {
                if (revision == null || revision.index < 0 || !indices.Add(revision.index)) throw new InvalidDataException("Version store contains invalid revision indices.");
                history.Add(new LuaScriptRevision(revision.index, revision.source ?? "", revision.utcTicks));
            }
            return new LuaScriptVersionRecord(slot.scriptKey.Trim(), slot.originalLua ?? "", slot.currentLua ?? "", history);
        }
        private static string Serialize(MemoryLuaScriptVersionStore memory)
        {
            PersistRootDto root = new();
            foreach (LuaScriptVersionRecord record in memory.ExportAllRecords())
            {
                PersistSlotDto slot = new() { scriptKey = record.ScriptKey, originalLua = record.OriginalLua, currentLua = record.CurrentLua };
                foreach (LuaScriptRevision revision in record.History) slot.history.Add(new PersistRevDto { index = revision.Index, source = revision.Source, utcTicks = revision.UtcTicks });
                root.slots.Add(slot);
            }
            string json = JsonConvert.SerializeObject(root, JsonSettings);
            if (Encoding.UTF8.GetByteCount(json) > MaxStoreBytes) throw new InvalidDataException($"Version store exceeds the {MaxStoreBytes}-byte limit.");
            return json;
        }
        private void WriteAtomically(string contents, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath));
            string temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, contents, new UTF8Encoding(false));
                BeforeAtomicReplaceForTesting?.Invoke();
                token.ThrowIfCancellationRequested();
                if (File.Exists(_filePath)) File.Replace(temporary, _filePath, null);
                else File.Move(temporary, _filePath);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException || failure is System.Security.SecurityException) { } }
        }
        // Field names preserve the existing JsonUtility file format for old installs.
        [Serializable] private sealed class PersistRootDto
        {
            [JsonProperty(Required = Required.Always)] public List<PersistSlotDto> slots = new();
        }
        [Serializable] private sealed class PersistSlotDto
        {
            public string scriptKey = "";
            public string originalLua = "";
            public string currentLua = "";
            public List<PersistRevDto> history = new();
        }
        [Serializable] private sealed class PersistRevDto { public int index; public string source = ""; public long utcTicks; }
    }
}
