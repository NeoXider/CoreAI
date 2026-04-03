using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>Персистентные оверлеи данных под <see cref="Application.persistentDataPath"/>/CoreAI/DataOverlayVersions/.</summary>
    public sealed class FileDataOverlayVersionStore : IDataOverlayVersionStore
    {
        private readonly MemoryDataOverlayVersionStore _memory = new();
        private readonly string _filePath;
        private readonly object _ioLock = new();

        public FileDataOverlayVersionStore()
        {
            var dir = Path.Combine(Application.persistentDataPath, "CoreAI", "DataOverlayVersions");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "data_overlays.json");
            LoadFromDisk();
        }

        public FileDataOverlayVersionStore(string jsonFilePath)
        {
            _filePath = jsonFilePath ?? throw new ArgumentNullException(nameof(jsonFilePath));
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            LoadFromDisk();
        }

        public bool TryGetSnapshot(string overlayKey, out DataOverlayVersionRecord snapshot) =>
            _memory.TryGetSnapshot(overlayKey, out snapshot);

        public void RecordSuccessfulApply(string overlayKey, string jsonOrTextPayload)
        {
            _memory.RecordSuccessfulApply(overlayKey, jsonOrTextPayload);
            SaveToDisk();
        }

        public void SeedOriginal(string overlayKey, string originalPayload, bool overwriteExistingOriginal = false)
        {
            _memory.SeedOriginal(overlayKey, originalPayload, overwriteExistingOriginal);
            SaveToDisk();
        }

        public void ResetToOriginal(string overlayKey)
        {
            _memory.ResetToOriginal(overlayKey);
            SaveToDisk();
        }

        public void ResetToRevision(string overlayKey, int revisionIndex)
        {
            _memory.ResetToRevision(overlayKey, revisionIndex);
            SaveToDisk();
        }

        public void ResetAllToOriginal()
        {
            _memory.ResetAllToOriginal();
            SaveToDisk();
        }

        public bool TryGetCurrentPayload(string overlayKey, out string currentPayload) =>
            _memory.TryGetCurrentPayload(overlayKey, out currentPayload);

        public IReadOnlyList<string> GetKnownKeys() => _memory.GetKnownKeys();

        public string BuildProgrammerPromptSection(string overlayKey) =>
            _memory.BuildProgrammerPromptSection(overlayKey);

        private void LoadFromDisk()
        {
            lock (_ioLock)
            {
                _memory.ClearAll();
                if (!File.Exists(_filePath))
                    return;
                try
                {
                    var json = File.ReadAllText(_filePath);
                    var dto = JsonUtility.FromJson<PersistRootDto>(json);
                    if (dto?.slots == null || dto.slots.Count == 0)
                        return;
                    var records = new List<DataOverlayVersionRecord>();
                    for (int i = 0; i < dto.slots.Count; i++)
                    {
                        var s = dto.slots[i];
                        if (s == null || string.IsNullOrWhiteSpace(s.overlayKey))
                            continue;
                        var hist = new List<LuaScriptRevision>();
                        if (s.history != null)
                        {
                            for (int h = 0; h < s.history.Count; h++)
                            {
                                var r = s.history[h];
                                if (r == null)
                                    continue;
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
                    Debug.LogWarning($"[CoreAI] Data overlay versions load failed, starting empty: {ex.Message}");
                    _memory.ClearAll();
                }
            }
        }

        private void SaveToDisk()
        {
            lock (_ioLock)
            {
                try
                {
                    var records = _memory.ExportAllRecords();
                    var root = new PersistRootDto { slots = new List<PersistSlotDto>() };
                    for (int i = 0; i < records.Count; i++)
                    {
                        var r = records[i];
                        var slot = new PersistSlotDto
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
                                var rev = r.History[h];
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

                    var json = JsonUtility.ToJson(root, true);
                    File.WriteAllText(_filePath, json);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CoreAI] Data overlay versions save failed: {ex.Message}");
                }
            }
        }

        [Serializable]
        private sealed class PersistRootDto
        {
            public List<PersistSlotDto> slots = new List<PersistSlotDto>();
        }

        [Serializable]
        private sealed class PersistSlotDto
        {
            public string overlayKey = "";
            public string originalPayload = "";
            public string currentPayload = "";
            public List<PersistRevDto> history = new List<PersistRevDto>();
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
