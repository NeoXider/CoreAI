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
    /// while a <c>manage_mods</c> load, reload, import or revert holds the gate is the AI's, and its content hash and the
    /// gate sequence of that change are remembered for the mod. Any other load (a Hub save, a startup restore, an undo
    /// or a world load putting an older source back) is the AI's only when it brings back a source the AI wrote. So "who
    /// made the last change" is read from the content, and an undo that restores the player's version makes the mod the
    /// player's again. A source that <c>/undo</c> or <c>/redo</c> put back is marked as the player's choice
    /// (<see cref="IsPutBackByPlayer"/>), whoever wrote it.
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
        private long _currentSequence;

        /// <summary>
        /// Raised with the trigger after an <c>/undo</c>, a <c>/redo</c> or a world load replaced the whole world
        /// (<see cref="IsWholeWorldReplacement"/>). Such a replacement starts the restored mods in a new runtime
        /// before it is published, so no per-mod load is reported: a listener compares the mods' sources itself
        /// (<see cref="NoteSourcePutBack"/>). Raised on the thread that finished the mutation, while the gate is held.
        /// </summary>
        public event Action<string> WholeWorldReplaced;

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

        /// <summary>True while an <c>/undo</c> or <c>/redo</c> holds the gate.</summary>
        public bool IsPlayerMoveRunning
        {
            get
            {
                lock (_gate)
                {
                    return IsPlayerMove(_currentTrigger);
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
                _currentSequence = mutation?.Sequence ?? 0L;
            }
        }

        /// <inheritdoc />
        public void OnMutationFinished(RbxWorldMutationInfo mutation, RbxWorldSnapshot before, bool completed)
        {
            lock (_gate)
            {
                _currentTrigger = "";
                _currentActorId = "";
                _currentSequence = 0L;
            }

            string trigger = mutation?.Trigger ?? "";
            if (!completed || !IsWholeWorldReplacement(trigger))
            {
                return;
            }

            Action<string> handlers = WholeWorldReplaced;
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<string>)handler)(trigger);
                }
                catch (Exception)
                {
                    // WHY: an observer must not throw into the gate; one failing listener must not starve the others.
                }
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
                bool changed = !record.HasHash || record.CurrentHash != hash;
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
                    record.AiSequence = _currentSequence;
                    record.PutBackByPlayer = false;
                    if (record.Request.Length == 0 && !string.IsNullOrWhiteSpace(request))
                    {
                        record.Request = request.Trim();
                    }
                }
                else if (changed)
                {
                    record.PutBackByPlayer = IsPlayerMove(_currentTrigger);
                }

                record.CurrentHash = hash;
                record.HasHash = true;
                record.Current = record.AiHashes.Contains(hash) ? LuaModAuthor.Ai : LuaModAuthor.Other;
                return record.Current;
            }
        }

        /// <summary>
        /// Records the source a mod runs after a whole-world replacement (<see cref="WholeWorldReplaced"/>):
        /// <paramref name="byPlayer"/> true for an <c>/undo</c> or <c>/redo</c>, false for a world load. Returns true
        /// when the source differs from the one the mod ran before, so the replacement changed the mod; a source the
        /// player's move changed is then marked <see cref="IsPutBackByPlayer"/>.
        /// </summary>
        public bool NoteSourcePutBack(string modId, string source, bool byPlayer)
        {
            string id = (modId ?? "").Trim();
            if (id.Length == 0)
            {
                return false;
            }

            ulong hash = Hash(source);
            lock (_gate)
            {
                Record record = GetOrCreate(id);
                bool changed = !record.HasHash || record.CurrentHash != hash;
                if (changed)
                {
                    record.PutBackByPlayer = byPlayer;
                    record.CurrentHash = hash;
                    record.HasHash = true;
                    record.Current = record.AiHashes.Contains(hash) ? LuaModAuthor.Ai : LuaModAuthor.Other;
                }

                return changed;
            }
        }

        /// <summary>
        /// The mod no longer runs after a whole-world replacement: the source it ran is forgotten (what the AI wrote is
        /// kept), so the version a later <c>/redo</c> or world load brings back counts as a change.
        /// </summary>
        public void NoteSourceGone(string modId)
        {
            lock (_gate)
            {
                if (_records.TryGetValue((modId ?? "").Trim(), out Record record))
                {
                    record.HasHash = false;
                    record.PutBackByPlayer = false;
                }
            }
        }

        /// <summary>Every mod whose current source the tracker knows.</summary>
        public IReadOnlyList<string> TrackedModIds
        {
            get
            {
                lock (_gate)
                {
                    List<string> ids = new(_records.Count);
                    foreach (KeyValuePair<string, Record> pair in _records)
                    {
                        if (pair.Value.HasHash)
                        {
                            ids.Add(pair.Key);
                        }
                    }

                    return ids;
                }
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

        /// <summary>
        /// True when the mod runs a source an <c>/undo</c> or <c>/redo</c> put back: the player chose that version, so
        /// the repair loop leaves it alone until the player asks or the mod changes again.
        /// </summary>
        public bool IsPutBackByPlayer(string modId)
        {
            lock (_gate)
            {
                return _records.TryGetValue((modId ?? "").Trim(), out Record record) && record.PutBackByPlayer;
            }
        }

        /// <summary>
        /// The gate sequence (<see cref="RbxWorldMutationInfo.Sequence"/>) of the AI change that wrote the source the
        /// mod runs now; 0 when the AI did not write it or the tracker did not see the change.
        /// </summary>
        public long AiSequenceFor(string modId)
        {
            lock (_gate)
            {
                return _records.TryGetValue((modId ?? "").Trim(), out Record record) && record.Current == LuaModAuthor.Ai
                    ? record.AiSequence
                    : 0L;
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

        /// <summary>True for the triggers that replace the whole world: <c>/undo</c>, <c>/redo</c> and a world load.</summary>
        public static bool IsWholeWorldReplacement(string trigger)
        {
            return IsPlayerMove(trigger) ||
                   string.Equals(trigger, RbxWorldRuntimeSessionController.PreLoadAutosaveTrigger, StringComparison.Ordinal);
        }

        /// <summary>True for the triggers of <c>/undo</c> and <c>/redo</c>.</summary>
        public static bool IsPlayerMove(string trigger)
        {
            return string.Equals(trigger, RbxWorldUndoService.UndoTrigger, StringComparison.Ordinal) ||
                   string.Equals(trigger, RbxWorldUndoService.RedoTrigger, StringComparison.Ordinal);
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
            public long AiSequence;
            public ulong CurrentHash;
            public bool HasHash;
            public bool PutBackByPlayer;
        }
    }
}
