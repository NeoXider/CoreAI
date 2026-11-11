using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreAI.Mods.Rbx.Instances.Networking
{
    /// <summary>
    /// The data of Roblox's player list (S2.5): one row per listed Player, one column per value object in the
    /// Player's child named exactly <c>leaderstats</c> (any class, matched by name): <c>IntValue</c>,
    /// <c>NumberValue</c> and <c>StringValue</c>, the column header being the object's <c>Name</c>, in creation order.
    /// Engine-free; <c>RbxPlayerListView</c> draws it. More players are more rows, nothing else changes.
    /// </summary>
    /// <remarks>
    /// Nothing is read on an unchanged frame beyond a comparison per watched object: a reparent anywhere near a
    /// Player (the registry's synchronous <c>ParentChanged</c>) or a destroyed watched object marks the structure
    /// dirty, and <see cref="Refresh"/> rebuilds it then (allocating only then); otherwise it compares each watched
    /// name and value with the one it last read, so an idle list allocates nothing.
    /// </remarks>
    public sealed class RbxPlayerList : IDisposable
    {
        /// <summary>The exact name of the stats container under a Player.</summary>
        public const string LeaderstatsName = "leaderstats";

        /// <summary>Columns shown at most by default.</summary>
        public const int DefaultMaxColumns = 4;

        /// <summary>Characters of a StringValue shown at most.</summary>
        public const int MaxTextCharacters = 32;

        private sealed class Stat
        {
            public RbxValueBase Value;
            public string Name;
            public long IntValue;
            public double NumberValue;
            public string StringValue;
            public string Text;
            public double SortKey;
            public bool HasSortKey;
        }

        private sealed class Row
        {
            public RbxPlayer Player;
            public string DisplayName;
            public readonly List<RbxInstance> Children = new();
            public readonly List<string> ChildNames = new();
            public readonly List<Stat> Stats = new();
            public string[] Cells = Array.Empty<string>();
        }

        private sealed class RowOrder : IComparer<Row>
        {
            public static readonly RowOrder Instance = new();

            public int Compare(Row left, Row right)
            {
                double leftKey = SortKey(left, out bool leftHas);
                double rightKey = SortKey(right, out bool rightHas);
                if (leftHas != rightHas)
                {
                    return leftHas ? -1 : 1;
                }

                if (leftHas && !leftKey.Equals(rightKey))
                {
                    return rightKey.CompareTo(leftKey);
                }

                int byName = string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : left.Player.UserId.CompareTo(right.Player.UserId);
            }

            private static double SortKey(Row row, out bool has)
            {
                Stat first = row.Stats.Count > 0 ? row.Stats[0] : null;
                has = first != null && first.HasSortKey;
                return has ? first.SortKey : 0d;
            }
        }

        private readonly InstanceRegistry _registry;
        private readonly RbxPlayers _players;
        private readonly int _maxColumns;
        private readonly HashSet<RbxInstance> _watched = new();
        private readonly List<Row> _rows = new();
        private readonly List<string> _columns = new();
        private readonly Action<RbxInstance> _onParentChanged;
        private readonly Action<InstanceRecord> _onUnregistered;
        private bool _structureDirty = true;
        private bool _disposed;

        /// <summary>Creates the list of one world.</summary>
        /// <param name="registry">The world's instances.</param>
        /// <param name="players">The world's Players service.</param>
        /// <param name="maxColumns">Stat columns shown at most.</param>
        public RbxPlayerList(InstanceRegistry registry, RbxPlayers players, int maxColumns = DefaultMaxColumns)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _players = players ?? throw new ArgumentNullException(nameof(players));
            _maxColumns = Math.Max(1, maxColumns);
            _onParentChanged = OnParentChanged;
            _onUnregistered = OnUnregistered;
            _registry.ParentChanged += _onParentChanged;
            _registry.Unregistered += _onUnregistered;
        }

        /// <summary>One more on every visible change; a view redraws when it differs from the last one it drew.</summary>
        public int Version { get; private set; }

        /// <summary>The stat column headers, in creation order (at most the column cap).</summary>
        public IReadOnlyList<string> Columns => _columns;

        /// <summary>The listed players, highest first value first.</summary>
        public int RowCount => _rows.Count;

        /// <summary>True when at least one row has a stat: the list has something a player list adds to a solo game.</summary>
        public bool HasStats => _columns.Count > 0;

        /// <summary>The Player of row <paramref name="row"/>.</summary>
        public RbxPlayer RowPlayer(int row)
        {
            return _rows[row].Player;
        }

        /// <summary>The name shown for row <paramref name="row"/> (the Player's DisplayName).</summary>
        public string RowName(int row)
        {
            return _rows[row].DisplayName ?? string.Empty;
        }

        /// <summary>The text of column <paramref name="column"/> in row <paramref name="row"/>, "" when the player has no such stat.</summary>
        public string Cell(int row, int column)
        {
            string[] cells = _rows[row].Cells;
            return column < cells.Length ? cells[column] ?? string.Empty : string.Empty;
        }

        /// <summary>Marks the structure for a rebuild on the next <see cref="Refresh"/>.</summary>
        public void MarkDirty()
        {
            _structureDirty = true;
        }

        /// <summary>
        /// Brings the list up to date with the world; true when anything visible changed (and <see cref="Version"/>
        /// moved). Allocates nothing when nothing changed.
        /// </summary>
        public bool Refresh()
        {
            if (_disposed)
            {
                return false;
            }

            bool changed = false;
            if (!_structureDirty)
            {
                changed = ReadValues();
            }

            if (_structureDirty)
            {
                Rebuild();
                Version++;
                return true;
            }

            if (changed)
            {
                if (_rows.Count > 1)
                {
                    _rows.Sort(RowOrder.Instance);
                }

                Version++;
            }

            return changed;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registry.ParentChanged -= _onParentChanged;
            _registry.Unregistered -= _onUnregistered;
            _rows.Clear();
            _columns.Clear();
            _watched.Clear();
        }

        /// <summary>The text a value object shows: invariant numbers, whole NumberValues without a fraction.</summary>
        public static string Format(RbxValueBase value)
        {
            switch (value)
            {
                case RbxIntValue intValue:
                    return intValue.Value.ToString(CultureInfo.InvariantCulture);
                case RbxNumberValue numberValue:
                    return FormatNumber(numberValue.Value);
                case RbxStringValue stringValue:
                    return Shorten(stringValue.Value);
                default:
                    return string.Empty;
            }
        }

        private static string FormatNumber(double number)
        {
            if (double.IsNaN(number))
            {
                return "NaN";
            }

            if (double.IsInfinity(number))
            {
                return number > 0d ? "inf" : "-inf";
            }

            if (number == Math.Floor(number) && Math.Abs(number) < 1e15)
            {
                return ((long)number).ToString(CultureInfo.InvariantCulture);
            }

            return number.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Shorten(string text)
        {
            string value = text ?? string.Empty;
            return value.Length <= MaxTextCharacters ? value : value.Substring(0, MaxTextCharacters - 3) + "...";
        }

        private static bool IsStat(RbxInstance instance)
        {
            return instance is RbxIntValue || instance is RbxNumberValue || instance is RbxStringValue;
        }

        private void OnParentChanged(RbxInstance instance)
        {
            if (_disposed || _structureDirty || instance == null)
            {
                return;
            }

            RbxInstance parent = instance.Parent;
            if (instance is RbxPlayer || _watched.Contains(instance)
                                      || (parent != null && (parent is RbxPlayer || _watched.Contains(parent))))
            {
                _structureDirty = true;
            }
        }

        private void OnUnregistered(InstanceRecord record)
        {
            if (!_disposed && !_structureDirty && record?.Instance != null && _watched.Contains(record.Instance))
            {
                _structureDirty = true;
            }
        }

        private bool ReadValues()
        {
            bool changed = false;
            for (int rowIndex = 0; rowIndex < _rows.Count; rowIndex++)
            {
                Row row = _rows[rowIndex];
                if (row.Player.IsDestroyed)
                {
                    _structureDirty = true;
                    return changed;
                }

                string displayName = row.Player.DisplayName ?? row.Player.Name;
                if (!string.Equals(displayName, row.DisplayName, StringComparison.Ordinal))
                {
                    row.DisplayName = displayName;
                    changed = true;
                }

                for (int childIndex = 0; childIndex < row.Children.Count; childIndex++)
                {
                    RbxInstance child = row.Children[childIndex];
                    if (child.IsDestroyed || !string.Equals(child.Name, row.ChildNames[childIndex], StringComparison.Ordinal))
                    {
                        _structureDirty = true;
                        return changed;
                    }
                }

                for (int statIndex = 0; statIndex < row.Stats.Count; statIndex++)
                {
                    Stat stat = row.Stats[statIndex];
                    if (stat.Value.IsDestroyed || !string.Equals(stat.Value.Name, stat.Name, StringComparison.Ordinal))
                    {
                        _structureDirty = true;
                        return changed;
                    }

                    if (ReadStat(stat))
                    {
                        int column = _columns.IndexOf(stat.Name);
                        if (column >= 0 && column < row.Cells.Length)
                        {
                            row.Cells[column] = stat.Text;
                        }

                        changed = true;
                    }
                }
            }

            return changed;
        }

        private static bool ReadStat(Stat stat)
        {
            switch (stat.Value)
            {
                case RbxIntValue intValue:
                    long integer = intValue.Value;
                    if (stat.Text != null && integer == stat.IntValue)
                    {
                        return false;
                    }

                    stat.IntValue = integer;
                    stat.SortKey = integer;
                    stat.HasSortKey = true;
                    break;
                case RbxNumberValue numberValue:
                    double number = numberValue.Value;
                    if (stat.Text != null && number.Equals(stat.NumberValue))
                    {
                        return false;
                    }

                    stat.NumberValue = number;
                    stat.SortKey = number;
                    stat.HasSortKey = !double.IsNaN(number);
                    break;
                case RbxStringValue stringValue:
                    string text = stringValue.Value ?? string.Empty;
                    if (stat.Text != null && string.Equals(text, stat.StringValue, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    stat.StringValue = text;
                    stat.HasSortKey = false;
                    break;
                default:
                    return false;
            }

            stat.Text = Format(stat.Value);
            return true;
        }

        private void Rebuild()
        {
            _structureDirty = false;
            _rows.Clear();
            _columns.Clear();
            _watched.Clear();
            if (_players.IsDestroyed)
            {
                return;
            }

            IReadOnlyList<RbxPlayer> players = _players.GetPlayers();
            for (int playerIndex = 0; playerIndex < players.Count; playerIndex++)
            {
                RbxPlayer player = players[playerIndex];
                if (player == null || player.IsDestroyed)
                {
                    continue;
                }

                Row row = new() { Player = player, DisplayName = player.DisplayName ?? player.Name };
                _watched.Add(player);
                RbxInstance leaderstats = null;
                IReadOnlyList<RbxInstance> children = player.GetChildren();
                for (int childIndex = 0; childIndex < children.Count; childIndex++)
                {
                    RbxInstance child = children[childIndex];
                    row.Children.Add(child);
                    row.ChildNames.Add(child.Name);
                    _watched.Add(child);
                    if (leaderstats == null && string.Equals(child.Name, LeaderstatsName, StringComparison.Ordinal))
                    {
                        leaderstats = child;
                    }
                }

                if (leaderstats != null)
                {
                    IReadOnlyList<RbxInstance> values = leaderstats.GetChildren();
                    for (int valueIndex = 0; valueIndex < values.Count; valueIndex++)
                    {
                        RbxInstance value = values[valueIndex];
                        _watched.Add(value);
                        if (!IsStat(value))
                        {
                            continue;
                        }

                        Stat stat = new() { Value = (RbxValueBase)value, Name = value.Name };
                        ReadStat(stat);
                        row.Stats.Add(stat);
                        if (!_columns.Contains(stat.Name) && _columns.Count < _maxColumns)
                        {
                            _columns.Add(stat.Name);
                        }
                    }
                }

                _rows.Add(row);
            }

            for (int rowIndex = 0; rowIndex < _rows.Count; rowIndex++)
            {
                Row row = _rows[rowIndex];
                row.Cells = new string[_columns.Count];
                for (int statIndex = 0; statIndex < row.Stats.Count; statIndex++)
                {
                    Stat stat = row.Stats[statIndex];
                    int column = _columns.IndexOf(stat.Name);
                    if (column >= 0 && row.Cells[column] == null)
                    {
                        row.Cells[column] = stat.Text;
                    }
                }
            }

            if (_rows.Count > 1)
            {
                _rows.Sort(RowOrder.Instance);
            }
        }
    }
}
