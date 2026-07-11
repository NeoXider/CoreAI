using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// In-memory data overlay version store. History is bounded by <see cref="VersionRetentionPolicy"/>:
    /// only the original, the current, and the last N intermediate revisions (plus a total byte budget) are
    /// kept per key. Evicted revisions keep their original <see cref="LuaScriptRevision.Index"/> numbering
    /// (indices are never reassigned), so callers that reference a revision by index must not assume the
    /// index equals its position in <see cref="DataOverlayVersionRecord.History"/>.
    /// </summary>
    public sealed class MemoryDataOverlayVersionStore : IDataOverlayVersionStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
        private readonly int _maxIntermediateRevisions;
        private readonly long _maxTotalBytes;

        public MemoryDataOverlayVersionStore(
            int maxIntermediateRevisions = VersionRetentionPolicy.DefaultMaxIntermediateRevisions,
            long maxTotalBytes = VersionRetentionPolicy.DefaultMaxTotalBytes)
        {
            _maxIntermediateRevisions = maxIntermediateRevisions;
            _maxTotalBytes = maxTotalBytes;
        }

        private sealed class Slot
        {
            public string OriginalPayload = "";
            public string CurrentPayload = "";
            public readonly List<LuaScriptRevision> History = new();

            /// <summary>Next stable sequence number to assign; independent of History.Count so numbering stays stable across evictions.</summary>
            public int NextIndex;
        }

        public bool TryGetSnapshot(string overlayKey, out DataOverlayVersionRecord snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(overlayKey))
            {
                return false;
            }

            string key = overlayKey.Trim();
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

                snapshot = new DataOverlayVersionRecord(key, slot.OriginalPayload, slot.CurrentPayload, copy);
                return true;
            }
        }

        public void RecordSuccessfulApply(string overlayKey, string jsonOrTextPayload)
        {
            RecordSuccessfulApplyChanged(overlayKey, jsonOrTextPayload);
        }

        /// <summary>
        /// Same as <see cref="RecordSuccessfulApply"/> but reports whether a new revision was actually
        /// appended, so file-backed stores can skip a redundant disk write for a no-op apply.
        /// </summary>
        public bool RecordSuccessfulApplyChanged(string overlayKey, string jsonOrTextPayload)
        {
            if (string.IsNullOrWhiteSpace(overlayKey))
            {
                return false;
            }

            string key = overlayKey.Trim();
            string payload = jsonOrTextPayload ?? "";
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out Slot slot))
                {
                    slot = new Slot();
                    _slots[key] = slot;
                    slot.OriginalPayload = payload;
                    slot.CurrentPayload = payload;
                    slot.History.Add(new LuaScriptRevision(0, payload, now));
                    slot.NextIndex = 1;
                    return true;
                }

                if (string.Equals(slot.CurrentPayload, payload, StringComparison.Ordinal))
                {
                    return false;
                }

                int next = slot.NextIndex++;
                slot.History.Add(new LuaScriptRevision(next, payload, now));
                slot.CurrentPayload = payload;
                VersionRetentionPolicy.Enforce(slot.History, _maxIntermediateRevisions, _maxTotalBytes);
                return true;
            }
        }

        public void SeedOriginal(string overlayKey, string originalPayload, bool overwriteExistingOriginal = false)
        {
            SeedOriginalChanged(overlayKey, originalPayload, overwriteExistingOriginal);
        }

        /// <summary>Same as <see cref="SeedOriginal"/> but reports whether the store was actually mutated.</summary>
        public bool SeedOriginalChanged(string overlayKey, string originalPayload,
            bool overwriteExistingOriginal = false)
        {
            if (string.IsNullOrWhiteSpace(overlayKey))
            {
                return false;
            }

            string key = overlayKey.Trim();
            string seed = originalPayload ?? "";
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out Slot slot))
                {
                    slot = new Slot();
                    _slots[key] = slot;
                    slot.OriginalPayload = seed;
                    slot.CurrentPayload = seed;
                    slot.History.Add(new LuaScriptRevision(0, seed, now));
                    slot.NextIndex = 1;
                    return true;
                }

                if (overwriteExistingOriginal || string.IsNullOrEmpty(slot.OriginalPayload))
                {
                    slot.OriginalPayload = seed;
                    slot.CurrentPayload = seed;
                    slot.History.Clear();
                    slot.History.Add(new LuaScriptRevision(0, seed, now));
                    slot.NextIndex = 1;
                    return true;
                }

                return false;
            }
        }

        public void ResetToOriginal(string overlayKey)
        {
            ResetToOriginalChanged(overlayKey);
        }

        /// <summary>Same as <see cref="ResetToOriginal"/> but reports whether the store was actually mutated.</summary>
        public bool ResetToOriginalChanged(string overlayKey)
        {
            if (string.IsNullOrWhiteSpace(overlayKey))
            {
                return false;
            }

            string key = overlayKey.Trim();
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out Slot slot) || string.IsNullOrEmpty(slot.OriginalPayload))
                {
                    return false;
                }

                string o = slot.OriginalPayload;
                slot.CurrentPayload = o;
                slot.History.Clear();
                slot.History.Add(new LuaScriptRevision(0, o, now));
                slot.NextIndex = 1;
                return true;
            }
        }

        public void ResetToRevision(string overlayKey, int revisionIndex)
        {
            ResetToRevisionChanged(overlayKey, revisionIndex);
        }

        /// <summary>Same as <see cref="ResetToRevision"/> but reports whether the store was actually mutated.</summary>
        public bool ResetToRevisionChanged(string overlayKey, int revisionIndex)
        {
            if (string.IsNullOrWhiteSpace(overlayKey))
            {
                return false;
            }

            if (revisionIndex < 0)
            {
                return false;
            }

            string key = overlayKey.Trim();
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
                slot.CurrentPayload = rev.Source ?? "";
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

        public bool TryGetCurrentPayload(string overlayKey, out string currentPayload)
        {
            currentPayload = null;
            if (string.IsNullOrWhiteSpace(overlayKey))
            {
                return false;
            }

            string key = overlayKey.Trim();
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out Slot slot) || slot.History.Count == 0)
                {
                    return false;
                }

                currentPayload = slot.CurrentPayload ?? "";
                return true;
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

        public string BuildProgrammerPromptSection(string overlayKey)
        {
            if (string.IsNullOrWhiteSpace(overlayKey))
            {
                return "";
            }

            DataOverlayVersionRecord snap = null;
            if (TryGetSnapshot(overlayKey, out DataOverlayVersionRecord s))
            {
                snap = s;
            }

            return DataOverlayVersionPromptFormatter.Format(overlayKey, snap);
        }

        public void ClearAll()
        {
            lock (_lock)
            {
                _slots.Clear();
            }
        }

        public void ImportFromRecords(IEnumerable<DataOverlayVersionRecord> records)
        {
            if (records == null)
            {
                return;
            }

            lock (_lock)
            {
                _slots.Clear();
                foreach (DataOverlayVersionRecord r in records)
                {
                    if (r == null || string.IsNullOrWhiteSpace(r.OverlayKey))
                    {
                        continue;
                    }

                    string key = r.OverlayKey.Trim();
                    Slot slot = new()
                    {
                        OriginalPayload = r.OriginalPayload ?? "",
                        CurrentPayload = r.CurrentPayload ?? ""
                    };
                    if (r.History != null && r.History.Count > 0)
                    {
                        for (int i = 0; i < r.History.Count; i++)
                        {
                            slot.History.Add(r.History[i]);
                        }
                    }
                    else if (!string.IsNullOrEmpty(slot.CurrentPayload))
                    {
                        slot.History.Add(new LuaScriptRevision(0, slot.CurrentPayload, DateTime.UtcNow.Ticks));
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

        public List<DataOverlayVersionRecord> ExportAllRecords()
        {
            lock (_lock)
            {
                List<DataOverlayVersionRecord> list = new(_slots.Count);
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

                    list.Add(new DataOverlayVersionRecord(kv.Key, slot.OriginalPayload, slot.CurrentPayload, copy));
                }

                return list;
            }
        }
    }
}