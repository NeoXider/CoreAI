using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Logging;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>A failed overlay save; local replacement can precede a refused persistence-arm request.</summary>
    public sealed class DataOverlayPersistenceException : IOException
    {
        /// <summary>True when the local file replacement completed before persistence arming failed.</summary>
        public bool LocalCommitCompleted { get; }

        public DataOverlayPersistenceException(bool localCommitCompleted, Exception innerException)
            : base(localCommitCompleted
                ? "Data overlay file was committed locally, but persistence was not armed. Retry the operation."
                : "Data overlay file was not committed. Retry the operation.", innerException)
        {
            LocalCommitCompleted = localCommitCompleted;
        }
    }

    /// <summary>
    /// Persists data overlay version records to the local file system. History is bounded per key by
    /// <see cref="VersionRetentionPolicy"/> (original + current + last N intermediate revisions + a byte
    /// budget), so the serialized JSON size stays bounded over a session instead of growing without limit.
    /// A mutating call only rewrites the file when the in-memory store actually changed (a no-op apply is
    /// skipped); every real mutation still serializes the full per-store JSON file, which is acceptable
    /// because retention keeps that payload small.
    /// </summary>
    public sealed class FileDataOverlayVersionStore : IDataOverlayVersionStore
    {
        private readonly IGameLogger _logger;
        private readonly MemoryDataOverlayVersionStore _memory;
        private readonly string _filePath;

        /// <summary>The resolved storage file; internal test seam for storage-root checks.</summary>
        internal string StoragePath => _filePath;
        private readonly object _ioLock = new();
        private readonly int _maxIntermediateRevisions;
        private readonly long _maxTotalBytes;
        private bool _pendingConfirmation;
        internal static Action BeforeAtomicReplaceForTesting;
        internal static Func<bool> PersistenceArmingForTesting;

        public FileDataOverlayVersionStore(
            IGameLogger logger,
            int maxIntermediateRevisions = VersionRetentionPolicy.DefaultMaxIntermediateRevisions,
            long maxTotalBytes = VersionRetentionPolicy.DefaultMaxTotalBytes)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _memory = new MemoryDataOverlayVersionStore(maxIntermediateRevisions, maxTotalBytes);
            _maxIntermediateRevisions = maxIntermediateRevisions;
            _maxTotalBytes = maxTotalBytes;
            string dir = CoreAiStorageRoot.Combine(CoreAiPersistentPaths.DataOverlayVersions);
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "data_overlays.json");
            LoadFromDisk();
        }

        public FileDataOverlayVersionStore(
            IGameLogger logger,
            string jsonFilePath,
            int maxIntermediateRevisions = VersionRetentionPolicy.DefaultMaxIntermediateRevisions,
            long maxTotalBytes = VersionRetentionPolicy.DefaultMaxTotalBytes)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _memory = new MemoryDataOverlayVersionStore(maxIntermediateRevisions, maxTotalBytes);
            _maxIntermediateRevisions = maxIntermediateRevisions;
            _maxTotalBytes = maxTotalBytes;
            _filePath = jsonFilePath ?? throw new ArgumentNullException(nameof(jsonFilePath));
            string dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            LoadFromDisk();
        }

        public bool TryGetSnapshot(string overlayKey, out DataOverlayVersionRecord snapshot)
        {
            lock (_ioLock) { return _memory.TryGetSnapshot(overlayKey, out snapshot); }
        }

        public void RecordSuccessfulApply(string overlayKey, string jsonOrTextPayload)
        {
            Mutate(staged => staged.RecordSuccessfulApplyChanged(overlayKey, jsonOrTextPayload));
        }

        public void SeedOriginal(string overlayKey, string originalPayload, bool overwriteExistingOriginal = false)
        {
            Mutate(staged => staged.SeedOriginalChanged(overlayKey, originalPayload, overwriteExistingOriginal));
        }

        public void ResetToOriginal(string overlayKey)
        {
            Mutate(staged => staged.ResetToOriginalChanged(overlayKey));
        }

        public void ResetToRevision(string overlayKey, int revisionIndex)
        {
            Mutate(staged => staged.ResetToRevisionChanged(overlayKey, revisionIndex));
        }

        public void ResetAllToOriginal()
        {
            Mutate(staged => { staged.ResetAllToOriginal(); return true; });
        }

        public bool TryGetCurrentPayload(string overlayKey, out string currentPayload)
        {
            lock (_ioLock) { return _memory.TryGetCurrentPayload(overlayKey, out currentPayload); }
        }

        public IReadOnlyList<string> GetKnownKeys()
        {
            lock (_ioLock) { return _memory.GetKnownKeys(); }
        }

        public string BuildProgrammerPromptSection(string overlayKey)
        {
            lock (_ioLock) { return _memory.BuildProgrammerPromptSection(overlayKey); }
        }

        private void Mutate(Func<MemoryDataOverlayVersionStore, bool> mutation)
        {
            lock (_ioLock)
            {
                MemoryDataOverlayVersionStore staged = new(_maxIntermediateRevisions, _maxTotalBytes);
                staged.ImportFromRecords(_memory.ExportAllRecords());
                if (!mutation(staged) && !_pendingConfirmation)
                {
                    return;
                }

                SaveToDisk(staged);
                _memory.ImportFromRecords(staged.ExportAllRecords());
            }
        }

        private void LoadFromDisk()
        {
            lock (_ioLock)
            {
                _memory.ClearAll();
                if (!File.Exists(_filePath))
                {
                    return;
                }

                try
                {
                    string json = File.ReadAllText(_filePath);
                    PersistRootDto dto = JsonUtility.FromJson<PersistRootDto>(json);
                    if (dto?.slots == null || dto.slots.Count == 0)
                    {
                        return;
                    }

                    List<DataOverlayVersionRecord> records = new();
                    for (int i = 0; i < dto.slots.Count; i++)
                    {
                        PersistSlotDto s = dto.slots[i];
                        if (s == null || string.IsNullOrWhiteSpace(s.overlayKey))
                        {
                            continue;
                        }

                        List<LuaScriptRevision> hist = new();
                        if (s.history != null)
                        {
                            for (int h = 0; h < s.history.Count; h++)
                            {
                                PersistRevDto r = s.history[h];
                                if (r == null)
                                {
                                    continue;
                                }

                                hist.Add(new LuaScriptRevision(r.index, r.payload ?? "", r.utcTicks));
                            }
                        }

                        records.Add(new DataOverlayVersionRecord(
                            s.overlayKey.Trim(),
                            s.originalPayload ?? "",
                            s.currentPayload ?? "",
                            hist));
                    }

                    _memory.ImportFromRecords(records);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        GameLogFeature.Core,
                        $"Data overlay versions load failed, starting empty: {ex.Message}");
                    _memory.ClearAll();
                }
            }
        }

        private void SaveToDisk(MemoryDataOverlayVersionStore staged)
        {
            lock (_ioLock)
            {
                bool localCommitCompleted = false;
                try
                {
                    List<DataOverlayVersionRecord> records = staged.ExportAllRecords();
                    PersistRootDto root = new() { slots = new List<PersistSlotDto>() };
                    for (int i = 0; i < records.Count; i++)
                    {
                        DataOverlayVersionRecord r = records[i];
                        PersistSlotDto slot = new()
                        {
                            overlayKey = r.OverlayKey,
                            originalPayload = r.OriginalPayload,
                            currentPayload = r.CurrentPayload,
                            history = new List<PersistRevDto>()
                        };
                        if (r.History != null)
                        {
                            for (int h = 0; h < r.History.Count; h++)
                            {
                                LuaScriptRevision rev = r.History[h];
                                slot.history.Add(new PersistRevDto
                                {
                                    index = rev.Index,
                                    payload = rev.Source,
                                    utcTicks = rev.UtcTicks
                                });
                            }
                        }

                        root.slots.Add(slot);
                    }

                    string json = JsonUtility.ToJson(root, true);
                    WriteAtomically(_filePath, json);
                    localCommitCompleted = true;
                    _pendingConfirmation = true;
                    if (!(PersistenceArmingForTesting?.Invoke() ?? CoreAiWebGlPersistence.Sync()))
                    {
                        throw new IOException("Automatic persistent-data storage is not armed.");
                    }

                    _pendingConfirmation = false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(GameLogFeature.Core, $"Data overlay versions save failed: {ex.Message}");
                    throw new DataOverlayPersistenceException(localCommitCompleted, ex);
                }
            }
        }

        private static void WriteAtomically(string path, string contents)
        {
            string tmpPath = path + ".tmp";
            try
            {
                File.WriteAllText(tmpPath, contents);
                BeforeAtomicReplaceForTesting?.Invoke();
                if (File.Exists(path))
                {
                    File.Replace(tmpPath, path, null);
                }
                else
                {
                    File.Move(tmpPath, path);
                }
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
        }

        [Serializable]
        private sealed class PersistRootDto
        {
            public List<PersistSlotDto> slots = new();
        }

        [Serializable]
        private sealed class PersistSlotDto
        {
            public string overlayKey = "";
            public string originalPayload = "";
            public string currentPayload = "";
            public List<PersistRevDto> history = new();
        }

        [Serializable]
        private sealed class PersistRevDto
        {
            public int index;
            public string payload = "";
            public long utcTicks;
        }
    }
}
