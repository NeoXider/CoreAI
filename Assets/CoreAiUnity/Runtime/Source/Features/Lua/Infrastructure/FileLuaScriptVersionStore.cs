using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Персистентное хранилище версий Lua под <see cref="Application.persistentDataPath"/>/CoreAI/LuaScriptVersions/.
    /// </summary>
    public sealed class FileLuaScriptVersionStore : ILuaScriptVersionStore
    {
        private readonly IGameLogger _logger;
        private readonly MemoryLuaScriptVersionStore _memory = new();
        private readonly string _filePath;
        private readonly object _ioLock = new();

        public FileLuaScriptVersionStore(IGameLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            string dir = Path.Combine(Application.persistentDataPath, "CoreAI", "LuaScriptVersions");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "lua_script_versions.json");
            LoadFromDisk();
        }

        /// <summary>Для тестов редактора: явный путь к файлу.</summary>
        public FileLuaScriptVersionStore(IGameLogger logger, string jsonFilePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _filePath = jsonFilePath ?? throw new ArgumentNullException(nameof(jsonFilePath));
            string dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            LoadFromDisk();
        }

        public bool TryGetSnapshot(string scriptKey, out LuaScriptVersionRecord snapshot)
        {
            return _memory.TryGetSnapshot(scriptKey, out snapshot);
        }

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

        public IReadOnlyList<string> GetKnownKeys()
        {
            return _memory.GetKnownKeys();
        }

        public string BuildProgrammerPromptSection(string scriptKey)
        {
            return _memory.BuildProgrammerPromptSection(scriptKey);
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

                    List<LuaScriptVersionRecord> records = new();
                    for (int i = 0; i < dto.slots.Count; i++)
                    {
                        PersistSlotDto s = dto.slots[i];
                        if (s == null || string.IsNullOrWhiteSpace(s.scriptKey))
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
                    _logger.LogWarning(
                        GameLogFeature.Core,
                        $"Lua script versions load failed, starting empty: {ex.Message}");
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
                    List<LuaScriptVersionRecord> records = _memory.ExportAllRecords();
                    PersistRootDto root = new() { slots = new List<PersistSlotDto>() };
                    for (int i = 0; i < records.Count; i++)
                    {
                        LuaScriptVersionRecord r = records[i];
                        PersistSlotDto slot = new()
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
                                LuaScriptRevision rev = r.History[h];
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

                    string json = JsonUtility.ToJson(root, true);
                    File.WriteAllText(_filePath, json);
                }
                catch (Exception ex)
                {
                    _logger.LogError(GameLogFeature.Core, $"Lua script versions save failed: {ex.Message}");
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
            public string scriptKey = "";
            public string originalLua = "";
            public string currentLua = "";
            public List<PersistRevDto> history = new();
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