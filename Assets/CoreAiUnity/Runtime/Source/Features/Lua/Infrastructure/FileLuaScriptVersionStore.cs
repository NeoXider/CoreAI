using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Logging;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// File Lua Script Version Store component used by CoreAI.
    /// </summary>
    public sealed class FileLuaScriptVersionStore : ILuaScriptVersionStore
    {
        private readonly IGameLogger _logger;
        private readonly MemoryLuaScriptVersionStore _memory = new();
        private readonly string _filePath;
        private readonly object _ioLock = new();

        private bool _hasLoaded;
        private bool _lastFileExists;
        private DateTime _lastWriteTimeUtc;

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
            new(StringComparer.Ordinal);

        public FileLuaScriptVersionStore(IGameLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            string dir = Path.Combine(Application.persistentDataPath, CoreAiPersistentPaths.RootFolderName,
                CoreAiPersistentPaths.LuaScriptVersions);
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "lua_script_versions.json");
            ReadFromDisk(() => true);
        }

        /// <summary>Initializes a new instance of FileLuaScriptVersionStore.</summary>
        public FileLuaScriptVersionStore(IGameLogger logger, string jsonFilePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _filePath = jsonFilePath ?? throw new ArgumentNullException(nameof(jsonFilePath));
            string dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            ReadFromDisk(() => true);
        }

        public bool TryGetSnapshot(string scriptKey, out LuaScriptVersionRecord snapshot)
        {
            LuaScriptVersionRecord loaded = null;
            bool found = ReadFromDisk(() => _memory.TryGetSnapshot(scriptKey, out loaded));
            snapshot = loaded;
            return found;
        }

        public void RecordSuccessfulExecution(string scriptKey, string executedLuaSource)
        {
            MutateOnDisk(() => _memory.RecordSuccessfulExecution(scriptKey, executedLuaSource));
        }

        public void SeedOriginal(string scriptKey, string originalLuaSource, bool overwriteExistingOriginal = false)
        {
            MutateOnDisk(() => _memory.SeedOriginal(scriptKey, originalLuaSource, overwriteExistingOriginal));
        }

        public void ResetToOriginal(string scriptKey)
        {
            MutateOnDisk(() => _memory.ResetToOriginal(scriptKey));
        }

        public void ResetToRevision(string scriptKey, int revisionIndex)
        {
            MutateOnDisk(() => _memory.ResetToRevision(scriptKey, revisionIndex));
        }

        public void ResetAllToOriginal()
        {
            MutateOnDisk(() => _memory.ResetAllToOriginal());
        }

        public IReadOnlyList<string> GetKnownKeys()
        {
            return ReadFromDisk(() => _memory.GetKnownKeys());
        }

        public string BuildProgrammerPromptSection(string scriptKey)
        {
            return ReadFromDisk(() => _memory.BuildProgrammerPromptSection(scriptKey));
        }

        private void MutateOnDisk(Action mutation)
        {
            SemaphoreSlim gate = FileLocks.GetOrAdd(Path.GetFullPath(_filePath), _ => new SemaphoreSlim(1, 1));
            gate.Wait();
            try
            {
                LoadFromDisk();
                mutation();
                SaveToDisk();
            }
            finally
            {
                gate.Release();
            }
        }

        private T ReadFromDisk<T>(Func<T> read)
        {
            SemaphoreSlim gate = FileLocks.GetOrAdd(Path.GetFullPath(_filePath), _ => new SemaphoreSlim(1, 1));
            gate.Wait();
            try
            {
                LoadFromDisk();
                return read();
            }
            finally
            {
                gate.Release();
            }
        }

        private void LoadFromDisk()
        {
            lock (_ioLock)
            {
                bool exists = File.Exists(_filePath);
                DateTime writeTimeUtc = exists ? File.GetLastWriteTimeUtc(_filePath) : default;
                if (_hasLoaded && exists == _lastFileExists && writeTimeUtc == _lastWriteTimeUtc)
                {
                    return;
                }

                _hasLoaded = true;
                _lastFileExists = exists;
                _lastWriteTimeUtc = writeTimeUtc;

                _memory.ClearAll();
                if (!exists)
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
                        $"Lua script versions load failed, starting empty: {ex}");
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

                    _hasLoaded = true;
                    _lastFileExists = true;
                    _lastWriteTimeUtc = File.GetLastWriteTimeUtc(_filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(GameLogFeature.Core, $"Lua script versions save failed: {ex}");
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