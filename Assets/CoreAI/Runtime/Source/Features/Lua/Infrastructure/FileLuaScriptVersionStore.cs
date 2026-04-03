using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Персистентное хранилище версий Lua под <see cref="Application.persistentDataPath"/>/CoreAI/LuaScriptVersions/.
    /// </summary>
    public sealed class FileLuaScriptVersionStore : ILuaScriptVersionStore
    {
        private readonly MemoryLuaScriptVersionStore _memory = new();
        private readonly string _filePath;
        private readonly object _ioLock = new();

        public FileLuaScriptVersionStore()
        {
            var dir = Path.Combine(Application.persistentDataPath, "CoreAI", "LuaScriptVersions");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "lua_script_versions.json");
            LoadFromDisk();
        }

        /// <summary>Для тестов редактора: явный путь к файлу.</summary>
        public FileLuaScriptVersionStore(string jsonFilePath)
        {
            _filePath = jsonFilePath ?? throw new ArgumentNullException(nameof(jsonFilePath));
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            LoadFromDisk();
        }

        public bool TryGetSnapshot(string scriptKey, out LuaScriptVersionRecord snapshot) =>
            _memory.TryGetSnapshot(scriptKey, out snapshot);

        public void RecordSuccessfulExecution(string scriptKey, string executedLuaSource)
        {
            _memory.RecordSuccessfulExecution(scriptKey, executedLuaSource);
            SaveToDisk();
        }

        public void SeedOriginal(string scriptKey, string originalLuaSource, bool overwriteExistingOriginal = false)
        {
            _memory.SeedOriginal(scriptKey, originalLuaSource, overwriteExistingOriginal);
            SaveToDisk();
        }

        public void ResetToOriginal(string scriptKey)
        {
            _memory.ResetToOriginal(scriptKey);
            SaveToDisk();
        }

        public void ResetToRevision(string scriptKey, int revisionIndex)
        {
            _memory.ResetToRevision(scriptKey, revisionIndex);
            SaveToDisk();
        }

        public void ResetAllToOriginal()
        {
            _memory.ResetAllToOriginal();
            SaveToDisk();
        }

        public IReadOnlyList<string> GetKnownKeys() => _memory.GetKnownKeys();

        public string BuildProgrammerPromptSection(string scriptKey) =>
            _memory.BuildProgrammerPromptSection(scriptKey);

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
                    var records = new List<LuaScriptVersionRecord>();
                    for (int i = 0; i < dto.slots.Count; i++)
                    {
                        var s = dto.slots[i];
                        if (s == null || string.IsNullOrWhiteSpace(s.scriptKey))
                            continue;
                        var hist = new List<LuaScriptRevision>();
                        if (s.history != null)
                        {
                            for (int h = 0; h < s.history.Count; h++)
                            {
                                var r = s.history[h];
                                if (r == null)
                                    continue;
                                hist.Add(new LuaScriptRevision(r.index, r.source ?? "", r.utcTicks));
                            }
                        }

                        records.Add(new LuaScriptVersionRecord(
                            s.scriptKey.Trim(),
                            s.originalLua ?? "",
                            s.currentLua ?? "",
                            hist));
                    }

                    _memory.ImportFromRecords(records);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CoreAI] Lua script versions load failed, starting empty: {ex.Message}");
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
                            scriptKey = r.ScriptKey,
                            originalLua = r.OriginalLua,
                            currentLua = r.CurrentLua,
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
                                    source = rev.Source,
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
                    Debug.LogWarning($"[CoreAI] Lua script versions save failed: {ex.Message}");
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
            public string scriptKey = "";
            public string originalLua = "";
            public string currentLua = "";
            public List<PersistRevDto> history = new List<PersistRevDto>();
        }

        [Serializable]
        private sealed class PersistRevDto
        {
            public int index;
            public string source = "";
            public long utcTicks;
        }
    }
}
