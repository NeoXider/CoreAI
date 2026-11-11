using System;
using System.Collections.Generic;
using CoreAI.Mods.WorldPackages;

namespace CoreAI.Ai.SelfRepair
{
    /// <summary>Who wrote the source a mod runs now.</summary>
    public enum LuaModAuthor
    {
        /// <summary>The mod never loaded while the tracker watched.</summary>
        Unknown = 0,

        /// <summary>The AI wrote this exact source through its tools (a gated <c>manage_mods</c> load, reload, import or revert).</summary>
        Ai = 1,

        /// <summary>Anything else: a Hub save, a startup restore, a host, a bundled sample.</summary>
        Other = 2
    }

    /// <summary>
    /// Tells which mods run a source the AI wrote. It watches the shared world-mutation gate: a mod source that loads
    /// while a <c>manage_mods</c> load, reload, import or revert holds the gate is the AI's, and its content hash is
    /// remembered for the mod. Any other load (a Hub save, a startup restore, an undo or a world load putting an older
    /// source back) is the AI's only when it brings back a source the AI wrote. So "who made the last change" is read
    /// from the content, and an undo that restores the player's version makes the mod the player's again.
    /// </summary>
    /// <remarks>
    /// Per process: after a restart every mod restored from the store counts as <see cref="LuaModAuthor.Other"/> until
    /// the AI changes it again. Thread-safe. The gate serializes mutations, so one mutation is current at a time.
    /// </remarks>
    public sealed class LuaModAuthorshipTracker : IRbxWorldMutationObserver
    {
        private const int MaximumHashesPerMod = 8;
        private const int MaximumTrackedMods = 512;

        private static readonly string[] AiTriggers =
        {
            LuaModsLlmTool.LoadBackupTrigger,
            LuaModsLlmTool.ReloadBackupTrigger,
            LuaModsLlmTool.ImportBackupTrigger,
            LuaModsLlmTool.RevertBackupTrigger
        };

        private readonly object _gate = new();
        private readonly Dictionary<string, Record> _records = new(StringComparer.Ordinal);
        private string _currentTrigger = "";
        private string _currentActorId = "";

        /// <summary>True while an AI tool's gated mod mutation runs.</summary>
        public bool IsAiMutationRunning
        {
            get
            {
                lock (_gate)
                {
                    return IsAiTrigger(_currentTrigger);
                }
            }
        }

        /// <inheritdoc />
        public void OnMutationStarting(RbxWorldMutationInfo mutation)
        {
            lock (_gate)
            {
                _currentTrigger = mutation?.Trigger ?? "";
                _currentActorId = mutation?.ActorId ?? "";
            }
        }

        /// <inheritdoc />
        public void OnMutationFinished(RbxWorldMutationInfo mutation, RbxWorldSnapshot before, bool completed)
        {
            lock (_gate)
            {
                _currentTrigger = "";
                _currentActorId = "";
            }
        }

        /// <summary>
        /// Records a successful load or reload of <paramref name="modId"/> with <paramref name="source"/>.
        /// <paramref name="request"/> is the player's request behind it, kept only for an AI-written source when the
        /// mod has none yet. Returns who wrote the source and whether the AI wrote it just now.
        /// </summary>
        public LuaModAuthor NoteSourceLoaded(string modId, string source, string request, out bool writtenByAiNow)
        {
            writtenByAiNow = false;
            string id = (modId ?? "").Trim();
            if (id.Length == 0)
            {
                return LuaModAuthor.Unknown;
            }

            ulong hash = Hash(source);
            lock (_gate)
            {
                Record record = GetOrCreate(id);
                if (IsAiTrigger(_currentTrigger))
                {
                    writtenByAiNow = true;
                    if (!record.AiHashes.Contains(hash))
                    {
                        if (record.AiHashes.Count >= MaximumHashesPerMod)
                        {
                            record.AiHashes.RemoveAt(0);
                        }

                        record.AiHashes.Add(hash);
                    }

                    record.ActorId = _currentActorId;
                    if (record.Request.Length == 0 && !string.IsNullOrWhiteSpace(request))
                    {
                        record.Request = request.Trim();
                    }
                }

                record.Current = record.AiHashes.Contains(hash) ? LuaModAuthor.Ai : LuaModAuthor.Other;
                return record.Current;
            }
        }

        /// <summary>Who wrote the source the mod runs now.</summary>
        public LuaModAuthor AuthorOf(string modId)
        {
            lock (_gate)
            {
                return _records.TryGetValue((modId ?? "").Trim(), out Record record) ? record.Current : LuaModAuthor.Unknown;
            }
        }

        /// <summary>The player's request that produced the mod's AI-written source; empty when unknown.</summary>
        public string RequestFor(string modId)
        {
            lock (_gate)
            {
                return _records.TryGetValue((modId ?? "").Trim(), out Record record) ? record.Request : "";
            }
        }

        /// <summary>The actor the AI changes of the mod were attributed to; empty when unknown.</summary>
        public string ActorFor(string modId)
        {
            lock (_gate)
            {
                return _records.TryGetValue((modId ?? "").Trim(), out Record record) ? record.ActorId : "";
            }
        }

        /// <summary>FNV-1a 64-bit hash of the UTF-16 source.</summary>
        public static ulong Hash(string source)
        {
            ulong hash = 14695981039346656037UL;
            string text = source ?? "";
            for (int index = 0; index < text.Length; index++)
            {
                hash ^= text[index];
                hash *= 1099511628211UL;
            }

            return hash;
        }

        private static bool IsAiTrigger(string trigger)
        {
            if (string.IsNullOrEmpty(trigger))
            {
                return false;
            }

            for (int index = 0; index < AiTriggers.Length; index++)
            {
                if (string.Equals(trigger, AiTriggers[index], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private Record GetOrCreate(string id)
        {
            if (_records.TryGetValue(id, out Record record))
            {
                return record;
            }

            if (_records.Count >= MaximumTrackedMods)
            {
                foreach (KeyValuePair<string, Record> pair in _records)
                {
                    if (pair.Value.Current != LuaModAuthor.Ai)
                    {
                        _records.Remove(pair.Key);
                        break;
                    }
                }
            }

            record = new Record();
            _records[id] = record;
            return record;
        }

        private sealed class Record
        {
            public readonly List<ulong> AiHashes = new();
            public LuaModAuthor Current;
            public string ActorId = "";
            public string Request = "";
        }
    }
}
