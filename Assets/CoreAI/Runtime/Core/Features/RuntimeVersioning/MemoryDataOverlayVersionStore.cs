using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>In-memory версии JSON/текстовых оверлеев (тесты, хост без диска).</summary>
    public sealed class MemoryDataOverlayVersionStore : IDataOverlayVersionStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

        private sealed class Slot
        {
            public string OriginalPayload = "";
            public string CurrentPayload = "";
            public readonly List<LuaScriptRevision> History = new();
        }

        public bool TryGetSnapshot(string overlayKey, out DataOverlayVersionRecord snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(overlayKey))
                return false;
            var key = overlayKey.Trim();
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out var slot) || slot.History.Count == 0)
                    return false;
                var copy = new List<LuaScriptRevision>(slot.History.Count);
                for (int i = 0; i < slot.History.Count; i++)
                    copy.Add(slot.History[i]);
                snapshot = new DataOverlayVersionRecord(key, slot.OriginalPayload, slot.CurrentPayload, copy);
                return true;
            }
        }

        public void RecordSuccessfulApply(string overlayKey, string jsonOrTextPayload)
        {
            if (string.IsNullOrWhiteSpace(overlayKey))
                return;
            var key = overlayKey.Trim();
            var payload = jsonOrTextPayload ?? "";
            var now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out var slot))
                {
                    slot = new Slot();
                    _slots[key] = slot;
                    slot.OriginalPayload = payload;
                    slot.CurrentPayload = payload;
                    slot.History.Add(new LuaScriptRevision(0, payload, now));
                    return;
                }

                if (string.Equals(slot.CurrentPayload, payload, StringComparison.Ordinal))
                    return;

                int next = slot.History.Count;
                slot.History.Add(new LuaScriptRevision(next, payload, now));
                slot.CurrentPayload = payload;
            }
        }

        public void SeedOriginal(string overlayKey, string originalPayload, bool overwriteExistingOriginal = false)
        {
            if (string.IsNullOrWhiteSpace(overlayKey))
                return;
            var key = overlayKey.Trim();
            var seed = originalPayload ?? "";
            var now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out var slot))
                {
                    slot = new Slot();
                    _slots[key] = slot;
                    slot.OriginalPayload = seed;
                    slot.CurrentPayload = seed;
                    slot.History.Add(new LuaScriptRevision(0, seed, now));
                    return;
                }

                if (overwriteExistingOriginal || string.IsNullOrEmpty(slot.OriginalPayload))
                {
                    slot.OriginalPayload = seed;
                    slot.CurrentPayload = seed;
                    slot.History.Clear();
                    slot.History.Add(new LuaScriptRevision(0, seed, now));
                }
            }
        }

        public void ResetToOriginal(string overlayKey)
        {
            if (string.IsNullOrWhiteSpace(overlayKey))
                return;
            var key = overlayKey.Trim();
            var now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out var slot) || string.IsNullOrEmpty(slot.OriginalPayload))
                    return;
                var o = slot.OriginalPayload;
                slot.CurrentPayload = o;
                slot.History.Clear();
                slot.History.Add(new LuaScriptRevision(0, o, now));
            }
        }

        public void ResetToRevision(string overlayKey, int revisionIndex)
        {
            if (string.IsNullOrWhiteSpace(overlayKey))
                return;
            if (revisionIndex < 0)
                return;
            var key = overlayKey.Trim();
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out var slot) || slot.History.Count == 0)
                    return;
                if (revisionIndex >= slot.History.Count)
                    return;
                var rev = slot.History[revisionIndex];
                slot.CurrentPayload = rev.Source ?? "";
                if (slot.History.Count > revisionIndex + 1)
                    slot.History.RemoveRange(revisionIndex + 1, slot.History.Count - revisionIndex - 1);
            }
        }

        public void ResetAllToOriginal()
        {
            List<string> keys;
            lock (_lock)
            {
                keys = new List<string>(_slots.Count);
                foreach (var kv in _slots)
                    keys.Add(kv.Key);
            }

            for (int i = 0; i < keys.Count; i++)
                ResetToOriginal(keys[i]);
        }

        public bool TryGetCurrentPayload(string overlayKey, out string currentPayload)
        {
            currentPayload = null;
            if (string.IsNullOrWhiteSpace(overlayKey))
                return false;
            var key = overlayKey.Trim();
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out var slot) || slot.History.Count == 0)
                    return false;
                currentPayload = slot.CurrentPayload ?? "";
                return true;
            }
        }

        public IReadOnlyList<string> GetKnownKeys()
        {
            lock (_lock)
            {
                var list = new List<string>(_slots.Count);
                foreach (var kv in _slots)
                {
                    if (kv.Value.History.Count > 0)
                        list.Add(kv.Key);
                }

                list.Sort(StringComparer.Ordinal);
                return list;
            }
        }

        public string BuildProgrammerPromptSection(string overlayKey)
        {
            if (string.IsNullOrWhiteSpace(overlayKey))
                return "";
            DataOverlayVersionRecord snap = null;
            if (TryGetSnapshot(overlayKey, out var s))
                snap = s;
            return DataOverlayVersionPromptFormatter.Format(overlayKey, snap);
        }

        public void ClearAll()
        {
            lock (_lock)
                _slots.Clear();
        }

        public void ImportFromRecords(IEnumerable<DataOverlayVersionRecord> records)
        {
            if (records == null)
                return;
            lock (_lock)
            {
                _slots.Clear();
                foreach (var r in records)
                {
                    if (r == null || string.IsNullOrWhiteSpace(r.OverlayKey))
                        continue;
                    var key = r.OverlayKey.Trim();
                    var slot = new Slot
                    {
                        OriginalPayload = r.OriginalPayload ?? "",
                        CurrentPayload = r.CurrentPayload ?? ""
                    };
                    if (r.History != null && r.History.Count > 0)
                    {
                        for (int i = 0; i < r.History.Count; i++)
                            slot.History.Add(r.History[i]);
                    }
                    else if (!string.IsNullOrEmpty(slot.CurrentPayload))
                        slot.History.Add(new LuaScriptRevision(0, slot.CurrentPayload, DateTime.UtcNow.Ticks));

                    if (slot.History.Count > 0)
                        _slots[key] = slot;
                }
            }
        }

        public List<DataOverlayVersionRecord> ExportAllRecords()
        {
            lock (_lock)
            {
                var list = new List<DataOverlayVersionRecord>(_slots.Count);
                foreach (var kv in _slots)
                {
                    var slot = kv.Value;
                    if (slot.History.Count == 0)
                        continue;
                    var copy = new List<LuaScriptRevision>(slot.History.Count);
                    for (int i = 0; i < slot.History.Count; i++)
                        copy.Add(slot.History[i]);
                    list.Add(new DataOverlayVersionRecord(kv.Key, slot.OriginalPayload, slot.CurrentPayload, copy));
                }

                return list;
            }
        }
    }
}
