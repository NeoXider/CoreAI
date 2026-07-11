using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// In-memory Lua script version store with original/current revision tracking. History is bounded by
    /// <see cref="VersionRetentionPolicy"/>: only the original, the current, and the last N intermediate
    /// revisions (plus a total byte budget) are kept per key, so a long-running session cannot grow a
    /// key's history without limit. Evicted revisions keep their original <see cref="LuaScriptRevision.Index"/>
    /// numbering (indices are never reassigned), so callers that reference a revision by index must not
    /// assume the index equals its position in <see cref="LuaScriptVersionRecord.History"/>.
    /// </summary>
    public sealed class MemoryLuaScriptVersionStore : ILuaScriptVersionStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
        private readonly int _maxIntermediateRevisions;
        private readonly long _maxTotalBytes;

        public MemoryLuaScriptVersionStore(
            int maxIntermediateRevisions = VersionRetentionPolicy.DefaultMaxIntermediateRevisions,
            long maxTotalBytes = VersionRetentionPolicy.DefaultMaxTotalBytes)
        {
            _maxIntermediateRevisions = maxIntermediateRevisions;
            _maxTotalBytes = maxTotalBytes;
        }

        private sealed class Slot
        {
            public string OriginalLua = "";
            public string CurrentLua = "";
            public readonly List<LuaScriptRevision> History = new();

            /// <summary>Next stable sequence number to assign; independent of History.Count so numbering stays stable across evictions.</summary>
            public int NextIndex;
        }

        public bool TryGetSnapshot(string scriptKey, out LuaScriptVersionRecord snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(scriptKey))
            {
                return false;
            }

            string key = scriptKey.Trim();
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out Slot slot) || slot.History.Count == 0)
                {
                    return false;
                }

                List<LuaScriptRevision> copy = new(slot.History.Count);
                for (int i = 0; i < slot.History.Count; i++)
                {
                    copy.Add(slot.History[i]);
                }

                snapshot = new LuaScriptVersionRecord(key, slot.OriginalLua, slot.CurrentLua, copy);
                return true;
            }
        }

        public void RecordSuccessfulExecution(string scriptKey, string executedLuaSource)
        {
            RecordSuccessfulExecutionChanged(scriptKey, executedLuaSource);
        }

        /// <summary>
        /// Same as <see cref="RecordSuccessfulExecution"/> but reports whether a new revision was actually
        /// appended, so file-backed stores can skip a redundant disk write for a no-op reload.
        /// </summary>
        public bool RecordSuccessfulExecutionChanged(string scriptKey, string executedLuaSource)
        {
            if (string.IsNullOrWhiteSpace(scriptKey))
            {
                return false;
            }

            string key = scriptKey.Trim();
            string lua = executedLuaSource ?? "";
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out Slot slot))
                {
                    slot = new Slot();
                    _slots[key] = slot;
                    slot.OriginalLua = lua;
                    slot.CurrentLua = lua;
                    slot.History.Add(new LuaScriptRevision(0, lua, now));
                    slot.NextIndex = 1;
                    return true;
                }

                if (string.Equals(slot.CurrentLua, lua, StringComparison.Ordinal))
                {
                    return false;
                }

                int next = slot.NextIndex++;
                slot.History.Add(new LuaScriptRevision(next, lua, now));
                slot.CurrentLua = lua;
                VersionRetentionPolicy.Enforce(slot.History, _maxIntermediateRevisions, _maxTotalBytes);
                return true;
            }
        }

        public void SeedOriginal(string scriptKey, string originalLuaSource, bool overwriteExistingOriginal = false)
        {
            SeedOriginalChanged(scriptKey, originalLuaSource, overwriteExistingOriginal);
        }

        /// <summary>Same as <see cref="SeedOriginal"/> but reports whether the store was actually mutated.</summary>
        public bool SeedOriginalChanged(string scriptKey, string originalLuaSource,
            bool overwriteExistingOriginal = false)
        {
            if (string.IsNullOrWhiteSpace(scriptKey))
            {
                return false;
            }

            string key = scriptKey.Trim();
            string seed = originalLuaSource ?? "";
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out Slot slot))
                {
                    slot = new Slot();
                    _slots[key] = slot;
                    slot.OriginalLua = seed;
                    slot.CurrentLua = seed;
                    slot.History.Add(new LuaScriptRevision(0, seed, now));
                    slot.NextIndex = 1;
                    return true;
                }

                if (overwriteExistingOriginal || string.IsNullOrEmpty(slot.OriginalLua))
                {
                    slot.OriginalLua = seed;
                    slot.CurrentLua = seed;
                    slot.History.Clear();
                    slot.History.Add(new LuaScriptRevision(0, seed, now));
                    slot.NextIndex = 1;
                    return true;
                }

                return false;
            }
        }

        public void ResetToOriginal(string scriptKey)
        {
            ResetToOriginalChanged(scriptKey);
        }

        /// <summary>Same as <see cref="ResetToOriginal"/> but reports whether the store was actually mutated.</summary>
        public bool ResetToOriginalChanged(string scriptKey)
        {
            if (string.IsNullOrWhiteSpace(scriptKey))
            {
                return false;
            }

            string key = scriptKey.Trim();
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out Slot slot) || string.IsNullOrEmpty(slot.OriginalLua))
                {
                    return false;
                }

                string o = slot.OriginalLua;
                slot.CurrentLua = o;
                slot.History.Clear();
                slot.History.Add(new LuaScriptRevision(0, o, now));
                slot.NextIndex = 1;
                return true;
            }
        }

        public void ResetToRevision(string scriptKey, int revisionIndex)
        {
            ResetToRevisionChanged(scriptKey, revisionIndex);
        }

        /// <summary>Same as <see cref="ResetToRevision"/> but reports whether the store was actually mutated.</summary>
        public bool ResetToRevisionChanged(string scriptKey, int revisionIndex)
        {
            if (string.IsNullOrWhiteSpace(scriptKey))
            {
                return false;
            }

            if (revisionIndex < 0)
            {
                return false;
            }

            string key = scriptKey.Trim();
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out Slot slot) || slot.History.Count == 0)
                {
                    return false;
                }

                // Revision indices are stable sequence numbers, not positions: after retention eviction the
                // requested index may no longer sit at slot.History[revisionIndex], so it must be searched.
                int pos = -1;
                for (int i = 0; i < slot.History.Count; i++)
                {
                    if (slot.History[i].Index == revisionIndex)
                    {
                        pos = i;
                        break;
                    }
                }

                if (pos < 0)
                {
                    return false;
                }

                LuaScriptRevision rev = slot.History[pos];
                slot.CurrentLua = rev.Source ?? "";
                if (slot.History.Count > pos + 1)
                {
                    slot.History.RemoveRange(pos + 1, slot.History.Count - pos - 1);
                }

                slot.NextIndex = revisionIndex + 1;
                return true;
            }
        }

        public void ResetAllToOriginal()
        {
            List<string> keys;
            lock (_lock)
            {
                keys = new List<string>(_slots.Count);
                foreach (KeyValuePair<string, Slot> kv in _slots)
                {
                    keys.Add(kv.Key);
                }
            }

            for (int i = 0; i < keys.Count; i++)
            {
                ResetToOriginal(keys[i]);
            }
        }

        public IReadOnlyList<string> GetKnownKeys()
        {
            lock (_lock)
            {
                List<string> list = new(_slots.Count);
                foreach (KeyValuePair<string, Slot> kv in _slots)
                {
                    if (kv.Value.History.Count > 0)
                    {
                        list.Add(kv.Key);
                    }
                }

                list.Sort(StringComparer.Ordinal);
                return list;
            }
        }

        public string BuildProgrammerPromptSection(string scriptKey)
        {
            if (string.IsNullOrWhiteSpace(scriptKey))
            {
                return "";
            }

            LuaScriptVersionRecord snap = null;
            if (TryGetSnapshot(scriptKey, out LuaScriptVersionRecord s))
            {
                snap = s;
            }

            return LuaScriptVersionPromptFormatter.Format(scriptKey, snap);
        }

        /// <summary>Removes all stored Lua script version records.</summary>
        public void ClearAll()
        {
            lock (_lock)
            {
                _slots.Clear();
            }
        }

        /// <summary>Imports Lua script version records into the in-memory store.</summary>
        public void ImportFromRecords(IEnumerable<LuaScriptVersionRecord> records)
        {
            if (records == null)
            {
                return;
            }

            lock (_lock)
            {
                _slots.Clear();
                foreach (LuaScriptVersionRecord r in records)
                {
                    if (r == null || string.IsNullOrWhiteSpace(r.ScriptKey))
                    {
                        continue;
                    }

                    string key = r.ScriptKey.Trim();
                    Slot slot = new()
                    {
                        OriginalLua = r.OriginalLua ?? "",
                        CurrentLua = r.CurrentLua ?? ""
                    };
                    if (r.History != null && r.History.Count > 0)
                    {
                        for (int i = 0; i < r.History.Count; i++)
                        {
                            slot.History.Add(r.History[i]);
                        }
                    }
                    else if (!string.IsNullOrEmpty(slot.CurrentLua))
                    {
                        slot.History.Add(new LuaScriptRevision(0, slot.CurrentLua, DateTime.UtcNow.Ticks));
                    }

                    if (slot.History.Count > 0)
                    {
                        int maxIndex = 0;
                        for (int i = 0; i < slot.History.Count; i++)
                        {
                            if (slot.History[i].Index > maxIndex)
                            {
                                maxIndex = slot.History[i].Index;
                            }
                        }

                        slot.NextIndex = maxIndex + 1;
                        VersionRetentionPolicy.Enforce(slot.History, _maxIntermediateRevisions, _maxTotalBytes);
                        _slots[key] = slot;
                    }
                }
            }
        }

        /// <summary>Exports all Lua script version records from the in-memory store.</summary>
        public List<LuaScriptVersionRecord> ExportAllRecords()
        {
            lock (_lock)
            {
                List<LuaScriptVersionRecord> list = new(_slots.Count);
                foreach (KeyValuePair<string, Slot> kv in _slots)
                {
                    Slot slot = kv.Value;
                    if (slot.History.Count == 0)
                    {
                        continue;
                    }

                    List<LuaScriptRevision> copy = new(slot.History.Count);
                    for (int i = 0; i < slot.History.Count; i++)
                    {
                        copy.Add(slot.History[i]);
                    }

                    list.Add(new LuaScriptVersionRecord(kv.Key, slot.OriginalLua, slot.CurrentLua, copy));
                }

                return list;
            }
        }
    }
}
