using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>Потокобезопасное in-memory хранилище (тесты, хост без персистенции).</summary>
    public sealed class MemoryLuaScriptVersionStore : ILuaScriptVersionStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

        private sealed class Slot
        {
            public string OriginalLua = "";
            public string CurrentLua = "";
            public readonly List<LuaScriptRevision> History = new();
        }

        public bool TryGetSnapshot(string scriptKey, out LuaScriptVersionRecord snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(scriptKey))
                return false;
            var key = scriptKey.Trim();
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out var slot) || slot.History.Count == 0)
                    return false;
                var copy = new List<LuaScriptRevision>(slot.History.Count);
                for (int i = 0; i < slot.History.Count; i++)
                    copy.Add(slot.History[i]);
                snapshot = new LuaScriptVersionRecord(key, slot.OriginalLua, slot.CurrentLua, copy);
                return true;
            }
        }

        public void RecordSuccessfulExecution(string scriptKey, string executedLuaSource)
        {
            if (string.IsNullOrWhiteSpace(scriptKey))
                return;
            var key = scriptKey.Trim();
            var lua = executedLuaSource ?? "";
            var now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out var slot))
                {
                    slot = new Slot();
                    _slots[key] = slot;
                    slot.OriginalLua = lua;
                    slot.CurrentLua = lua;
                    slot.History.Add(new LuaScriptRevision(0, lua, now));
                    return;
                }

                if (string.Equals(slot.CurrentLua, lua, StringComparison.Ordinal))
                    return;

                int next = slot.History.Count;
                slot.History.Add(new LuaScriptRevision(next, lua, now));
                slot.CurrentLua = lua;
            }
        }

        public void SeedOriginal(string scriptKey, string originalLuaSource, bool overwriteExistingOriginal = false)
        {
            if (string.IsNullOrWhiteSpace(scriptKey))
                return;
            var key = scriptKey.Trim();
            var seed = originalLuaSource ?? "";
            var now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out var slot))
                {
                    slot = new Slot();
                    _slots[key] = slot;
                    slot.OriginalLua = seed;
                    slot.CurrentLua = seed;
                    slot.History.Add(new LuaScriptRevision(0, seed, now));
                    return;
                }

                if (overwriteExistingOriginal || string.IsNullOrEmpty(slot.OriginalLua))
                {
                    slot.OriginalLua = seed;
                    slot.CurrentLua = seed;
                    slot.History.Clear();
                    slot.History.Add(new LuaScriptRevision(0, seed, now));
                }
            }
        }

        public void ResetToOriginal(string scriptKey)
        {
            if (string.IsNullOrWhiteSpace(scriptKey))
                return;
            var key = scriptKey.Trim();
            var now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_slots.TryGetValue(key, out var slot) || string.IsNullOrEmpty(slot.OriginalLua))
                    return;
                var o = slot.OriginalLua;
                slot.CurrentLua = o;
                slot.History.Clear();
                slot.History.Add(new LuaScriptRevision(0, o, now));
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

        public string BuildProgrammerPromptSection(string scriptKey)
        {
            if (string.IsNullOrWhiteSpace(scriptKey))
                return "";
            LuaScriptVersionRecord snap = null;
            if (TryGetSnapshot(scriptKey, out var s))
                snap = s;
            return LuaScriptVersionPromptFormatter.Format(scriptKey, snap);
        }

        /// <summary>Сбросить все слоты (тесты / перед загрузкой с диска).</summary>
        public void ClearAll()
        {
            lock (_lock)
                _slots.Clear();
        }

        /// <summary>Заменить состояние из снимков (десериализация с диска).</summary>
        public void ImportFromRecords(IEnumerable<LuaScriptVersionRecord> records)
        {
            if (records == null)
                return;
            lock (_lock)
            {
                _slots.Clear();
                foreach (var r in records)
                {
                    if (r == null || string.IsNullOrWhiteSpace(r.ScriptKey))
                        continue;
                    var key = r.ScriptKey.Trim();
                    var slot = new Slot
                    {
                        OriginalLua = r.OriginalLua ?? "",
                        CurrentLua = r.CurrentLua ?? ""
                    };
                    if (r.History != null && r.History.Count > 0)
                    {
                        for (int i = 0; i < r.History.Count; i++)
                            slot.History.Add(r.History[i]);
                    }
                    else if (!string.IsNullOrEmpty(slot.CurrentLua))
                        slot.History.Add(new LuaScriptRevision(0, slot.CurrentLua, DateTime.UtcNow.Ticks));

                    if (slot.History.Count > 0)
                        _slots[key] = slot;
                }
            }
        }

        /// <summary>Все слоты для сериализации.</summary>
        public List<LuaScriptVersionRecord> ExportAllRecords()
        {
            lock (_lock)
            {
                var list = new List<LuaScriptVersionRecord>(_slots.Count);
                foreach (var kv in _slots)
                {
                    var slot = kv.Value;
                    if (slot.History.Count == 0)
                        continue;
                    var copy = new List<LuaScriptRevision>(slot.History.Count);
                    for (int i = 0; i < slot.History.Count; i++)
                        copy.Add(slot.History[i]);
                    list.Add(new LuaScriptVersionRecord(kv.Key, slot.OriginalLua, slot.CurrentLua, copy));
                }

                return list;
            }
        }
    }
}
