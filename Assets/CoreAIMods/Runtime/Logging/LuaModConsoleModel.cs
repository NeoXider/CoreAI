using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CoreAI.Ai.Logging
{
    /// <summary>Which severities the in-game console shows.</summary>
    public enum LuaModConsoleLevelFilter
    {
        /// <summary>Everything.</summary>
        All = 0,

        /// <summary>Warnings, errors and repair failures.</summary>
        WarningsAndErrors = 1,

        /// <summary>Errors only.</summary>
        Errors = 2
    }

    /// <summary>How a console row is drawn.</summary>
    public enum LuaModConsoleRowKind
    {
        /// <summary><c>print</c> output.</summary>
        Print = 0,

        /// <summary><c>warn</c> output.</summary>
        Warning = 1,

        /// <summary>An <c>error</c> call or a runtime error.</summary>
        Error = 2,

        /// <summary>A line of the self-repair loop.</summary>
        Repair = 3,

        /// <summary>A load, reload or unload.</summary>
        Lifecycle = 4
    }

    /// <summary>One row the console shows.</summary>
    public readonly struct LuaModConsoleRow
    {
        /// <summary>Creates a row.</summary>
        public LuaModConsoleRow(string text, LuaModConsoleRowKind kind, long sequence)
        {
            Text = text ?? "";
            Kind = kind;
            Sequence = sequence;
        }

        /// <summary>The rendered line.</summary>
        public string Text { get; }

        /// <summary>How it is drawn.</summary>
        public LuaModConsoleRowKind Kind { get; }

        /// <summary>The log sequence of the entry.</summary>
        public long Sequence { get; }
    }

    /// <summary>
    /// The engine-free model of the in-game mod console: a fixed ring of the newest log entries of every running mod
    /// (prints, warnings, errors with script and line, load and reload lines, the self-repair loop's lines), filtered
    /// by mod and by level, newest at the bottom, with a scroll window over older rows. Appending stores references in
    /// preallocated slots and allocates nothing, so a mod that prints every frame neither grows memory nor stalls the
    /// frame; a row's text is formatted once, only when a view asks for it. Thread-safe.
    /// </summary>
    public sealed class LuaModConsoleModel : IDisposable
    {
        /// <summary>Default number of entries kept.</summary>
        public const int DefaultCapacity = 512;

        /// <summary>Most distinct mods the mod filter cycles through.</summary>
        public const int MaximumKnownMods = 64;

        /// <summary>Characters of a message shown in one row.</summary>
        public const int MaximumMessageCharacters = 240;

        private readonly object _gate = new();
        private readonly Slot[] _slots;
        private readonly List<string> _mods = new();
        private readonly HashSet<string> _modSet = new(StringComparer.Ordinal);
        private readonly Action<LuaLogEntry> _onAppended;
        private ILuaLogService _source;
        private int _start;
        private int _count;
        private int _version;
        private int _scrollBack;
        private string _modFilter = "";
        private LuaModConsoleLevelFilter _levelFilter;
        private bool _isOpen;
        private long _dropped;

        /// <summary>Creates an empty model keeping <paramref name="capacity"/> entries.</summary>
        public LuaModConsoleModel(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _slots = new Slot[capacity];
            _onAppended = Append;
        }

        /// <summary>Entries the ring holds.</summary>
        public int Capacity => _slots.Length;

        /// <summary>Changes whenever a view should redraw.</summary>
        public int Version
        {
            get
            {
                lock (_gate)
                {
                    return _version;
                }
            }
        }

        /// <summary>Entries kept now.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _count;
                }
            }
        }

        /// <summary>Entries the ring overwrote since it was created or cleared.</summary>
        public long Dropped
        {
            get
            {
                lock (_gate)
                {
                    return _dropped;
                }
            }
        }

        /// <summary>True while the console is shown.</summary>
        public bool IsOpen
        {
            get
            {
                lock (_gate)
                {
                    return _isOpen;
                }
            }
        }

        /// <summary>The mod the rows are limited to; empty for every mod.</summary>
        public string ModFilter
        {
            get
            {
                lock (_gate)
                {
                    return _modFilter;
                }
            }
        }

        /// <summary>The severities shown.</summary>
        public LuaModConsoleLevelFilter LevelFilter
        {
            get
            {
                lock (_gate)
                {
                    return _levelFilter;
                }
            }
        }

        /// <summary>Rows scrolled back from the newest; 0 follows new entries.</summary>
        public int ScrollBack
        {
            get
            {
                lock (_gate)
                {
                    return _scrollBack;
                }
            }
        }

        /// <summary>The label of the level filter button.</summary>
        public string LevelFilterLabel
        {
            get
            {
                switch (LevelFilter)
                {
                    case LuaModConsoleLevelFilter.WarningsAndErrors:
                        return "Warnings+";
                    case LuaModConsoleLevelFilter.Errors:
                        return "Errors";
                    default:
                        return "All levels";
                }
            }
        }

        /// <summary>The label of the mod filter button.</summary>
        public string ModFilterLabel
        {
            get
            {
                string mod = ModFilter;
                return mod.Length == 0 ? "All mods" : "Mod: " + mod;
            }
        }

        /// <summary>Follows <paramref name="logs"/>: copies its newest entries, then every entry appended to it.</summary>
        public void Attach(ILuaLogService logs)
        {
            if (logs == null)
            {
                throw new ArgumentNullException(nameof(logs));
            }

            Detach();
            IReadOnlyList<LuaLogEntry> existing = logs.Query(new LuaLogQuery { MaxCount = _slots.Length });
            foreach (LuaLogEntry entry in existing)
            {
                Append(entry);
            }

            lock (_gate)
            {
                _source = logs;
            }

            logs.EntryAppended += _onAppended;
        }

        /// <summary>Stops following the log it was attached to.</summary>
        public void Detach()
        {
            ILuaLogService source;
            lock (_gate)
            {
                source = _source;
                _source = null;
            }

            if (source != null)
            {
                source.EntryAppended -= _onAppended;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Detach();
        }

        /// <summary>Adds one entry; the oldest is overwritten when the ring is full. Allocates nothing.</summary>
        public void Append(LuaLogEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            lock (_gate)
            {
                int index;
                if (_count == _slots.Length)
                {
                    index = _start;
                    _start = (_start + 1) % _slots.Length;
                    _dropped++;
                }
                else
                {
                    index = (_start + _count) % _slots.Length;
                    _count++;
                }

                _slots[index].Set(entry);
                string modId = entry.ModId ?? "";
                if (modId.Length > 0 && _mods.Count < MaximumKnownMods && _modSet.Add(modId))
                {
                    _mods.Add(modId);
                }

                if (_scrollBack > 0 && Matches(ref _slots[index]))
                {
                    _scrollBack++;
                }

                _version++;
            }
        }

        /// <summary>Shows or hides the console.</summary>
        public void SetOpen(bool open)
        {
            lock (_gate)
            {
                if (_isOpen == open)
                {
                    return;
                }

                _isOpen = open;
                _version++;
            }
        }

        /// <summary>Opens a closed console and closes an open one; returns the new state.</summary>
        public bool Toggle()
        {
            lock (_gate)
            {
                _isOpen = !_isOpen;
                _version++;
                return _isOpen;
            }
        }

        /// <summary>Steps the level filter: all, warnings and errors, errors, all.</summary>
        public void CycleLevelFilter()
        {
            lock (_gate)
            {
                _levelFilter = _levelFilter == LuaModConsoleLevelFilter.Errors
                    ? LuaModConsoleLevelFilter.All
                    : _levelFilter + 1;
                _scrollBack = 0;
                _version++;
            }
        }

        /// <summary>Sets the level filter.</summary>
        public void SetLevelFilter(LuaModConsoleLevelFilter filter)
        {
            lock (_gate)
            {
                _levelFilter = filter;
                _scrollBack = 0;
                _version++;
            }
        }

        /// <summary>Steps the mod filter through every mod seen, then back to all mods.</summary>
        public void CycleModFilter()
        {
            lock (_gate)
            {
                if (_mods.Count == 0)
                {
                    _modFilter = "";
                }
                else
                {
                    int at = _modFilter.Length == 0 ? -1 : _mods.IndexOf(_modFilter);
                    _modFilter = at + 1 >= _mods.Count ? "" : _mods[at + 1];
                }

                _scrollBack = 0;
                _version++;
            }
        }

        /// <summary>Limits the rows to one mod; empty shows every mod.</summary>
        public void SetModFilter(string modId)
        {
            lock (_gate)
            {
                _modFilter = (modId ?? "").Trim();
                _scrollBack = 0;
                _version++;
            }
        }

        /// <summary>Scrolls <paramref name="rows"/> further back (positive) or forward (negative); clamped.</summary>
        public void Scroll(int rows)
        {
            lock (_gate)
            {
                int matching = CountMatchingLocked();
                _scrollBack = Math.Max(0, Math.Min(Math.Max(0, matching - 1), _scrollBack + rows));
                _version++;
            }
        }

        /// <summary>Back to the newest rows.</summary>
        public void ScrollToNewest()
        {
            lock (_gate)
            {
                _scrollBack = 0;
                _version++;
            }
        }

        /// <summary>Forgets every entry (the mod log itself is untouched).</summary>
        public void Clear()
        {
            lock (_gate)
            {
                for (int index = 0; index < _slots.Length; index++)
                {
                    _slots[index] = default;
                }

                _start = 0;
                _count = 0;
                _dropped = 0;
                _scrollBack = 0;
                _version++;
            }
        }

        /// <summary>
        /// Fills <paramref name="rows"/> with the rows that match the filters, oldest first and newest last, ending
        /// <see cref="ScrollBack"/> rows before the newest. Returns how many it wrote; <paramref name="matching"/> is how
        /// many rows match in all.
        /// </summary>
        public int CopyVisible(LuaModConsoleRow[] rows, out int matching)
        {
            matching = 0;
            if (rows == null || rows.Length == 0)
            {
                return 0;
            }

            lock (_gate)
            {
                matching = CountMatchingLocked();
                int skipNewest = Math.Min(_scrollBack, Math.Max(0, matching - 1));
                int written = 0;
                int skipped = 0;
                for (int offset = _count - 1; offset >= 0 && written < rows.Length; offset--)
                {
                    int index = (_start + offset) % _slots.Length;
                    if (!Matches(ref _slots[index]))
                    {
                        continue;
                    }

                    if (skipped < skipNewest)
                    {
                        skipped++;
                        continue;
                    }

                    written++;
                }

                int position = written;
                skipped = 0;
                for (int offset = _count - 1; offset >= 0 && position > 0; offset--)
                {
                    int index = (_start + offset) % _slots.Length;
                    if (!Matches(ref _slots[index]))
                    {
                        continue;
                    }

                    if (skipped < skipNewest)
                    {
                        skipped++;
                        continue;
                    }

                    position--;
                    rows[position] = new LuaModConsoleRow(_slots[index].Text(), _slots[index].Kind,
                        _slots[index].Sequence);
                }

                return written;
            }
        }

        /// <summary>The text of a row as the console shows it.</summary>
        public static string FormatRow(LuaLogEntry entry)
        {
            if (entry == null)
            {
                return "";
            }

            Slot slot = default;
            slot.Set(entry);
            return slot.Text();
        }

        /// <summary>How an entry is drawn.</summary>
        public static LuaModConsoleRowKind KindOf(LuaLogEntry entry)
        {
            if (entry == null)
            {
                return LuaModConsoleRowKind.Print;
            }

            if (LuaModLifecycleLog.IsLifecycleEntry(entry))
            {
                return LuaModConsoleRowKind.Lifecycle;
            }

            if (string.Equals(entry.ScriptName, RepairScriptName, StringComparison.Ordinal))
            {
                return entry.Level >= LuaLogLevel.Warn ? LuaModConsoleRowKind.Warning : LuaModConsoleRowKind.Repair;
            }

            switch (entry.Level)
            {
                case LuaLogLevel.Warn:
                    return LuaModConsoleRowKind.Warning;
                case LuaLogLevel.Error:
                case LuaLogLevel.RuntimeError:
                    return LuaModConsoleRowKind.Error;
                default:
                    return LuaModConsoleRowKind.Print;
            }
        }

        /// <summary><c>ScriptName</c> of the self-repair loop's lines.</summary>
        public const string RepairScriptName = "coreai:repair";

        private int CountMatchingLocked()
        {
            int matching = 0;
            for (int offset = 0; offset < _count; offset++)
            {
                if (Matches(ref _slots[(_start + offset) % _slots.Length]))
                {
                    matching++;
                }
            }

            return matching;
        }

        private bool Matches(ref Slot slot)
        {
            if (_modFilter.Length > 0 && !string.Equals(slot.ModId, _modFilter, StringComparison.Ordinal))
            {
                return false;
            }

            switch (_levelFilter)
            {
                case LuaModConsoleLevelFilter.WarningsAndErrors:
                    return slot.Level >= LuaLogLevel.Warn;
                case LuaModConsoleLevelFilter.Errors:
                    return slot.Level >= LuaLogLevel.Error;
                default:
                    return true;
            }
        }

        private struct Slot
        {
            public long Sequence;
            public string ModId;
            public LuaLogLevel Level;
            public string Message;
            public string ScriptName;
            public int Line;
            public LuaModConsoleRowKind Kind;
            private string _text;

            public void Set(LuaLogEntry entry)
            {
                Sequence = entry.Sequence;
                ModId = entry.ModId ?? "";
                Level = entry.Level;
                Message = entry.Message ?? "";
                ScriptName = entry.ScriptName;
                Line = entry.Line ?? 0;
                Kind = KindOf(entry);
                _text = null;
            }

            public string Text()
            {
                if (_text != null)
                {
                    return _text;
                }

                StringBuilder text = new(Math.Min(Message.Length, MaximumMessageCharacters) + ModId.Length + 24);
                text.Append('[').Append(ModId.Length == 0 ? "host" : ModId).Append("] ");
                switch (Kind)
                {
                    case LuaModConsoleRowKind.Lifecycle:
                        text.Append(Message);
                        _text = text.ToString();
                        return _text;
                    case LuaModConsoleRowKind.Repair:
                        text.Append("repair: ");
                        break;
                    case LuaModConsoleRowKind.Warning:
                        text.Append(string.Equals(ScriptName, RepairScriptName, StringComparison.Ordinal)
                            ? "repair: "
                            : "warn: ");
                        break;
                    case LuaModConsoleRowKind.Error:
                        text.Append("error: ");
                        break;
                }

                if (!string.IsNullOrEmpty(ScriptName) && !ScriptName.StartsWith("coreai:", StringComparison.Ordinal))
                {
                    text.Append(ScriptName);
                    if (Line > 0)
                    {
                        text.Append(':').Append(Line.ToString(CultureInfo.InvariantCulture));
                    }

                    text.Append(' ');
                }

                string message = Message.Replace('\n', ' ').Replace('\r', ' ');
                text.Append(message.Length <= MaximumMessageCharacters
                    ? message
                    : message.Substring(0, MaximumMessageCharacters) + "...");
                _text = text.ToString();
                return _text;
            }
        }
    }
}
