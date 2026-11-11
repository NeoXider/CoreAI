using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>How the player list is drawn; a host changes it before the first world starts.</summary>
    public sealed class RbxPlayerListOptions
    {
        /// <summary>
        /// The USS of the list. The default puts it at the top right, 56 px down and 14 px in: below a status line or
        /// banner a HUD keeps at the top edge, above a key legend at the bottom right, and away from the chat at the
        /// bottom left.
        /// </summary>
        public const string DefaultUss =
            ".cai-playerlist { position: absolute; top: 56px; right: 14px; min-width: 200px; max-width: 45%; "
            + "padding: 4px 10px 6px 10px; border-radius: 6px; background-color: rgba(10, 14, 24, 0.62); }\n"
            + ".cai-playerlist__row { flex-direction: row; align-items: center; min-height: 22px; }\n"
            + ".cai-playerlist__header { min-height: 18px; border-bottom-width: 1px; "
            + "border-bottom-color: rgba(255, 255, 255, 0.22); margin-bottom: 2px; }\n"
            + ".cai-playerlist__name { flex-grow: 1; min-width: 96px; padding-right: 8px; font-size: 15px; "
            + "color: rgb(240, 240, 240); -unity-text-align: middle-left; overflow: hidden; }\n"
            + ".cai-playerlist__cell { width: 72px; font-size: 15px; color: rgb(255, 255, 255); "
            + "-unity-text-align: middle-right; -unity-font-style: bold; }\n"
            + ".cai-playerlist__header .cai-playerlist__cell { font-size: 12px; color: rgb(200, 206, 216); "
            + "-unity-font-style: normal; }\n"
            + ".cai-playerlist__local .cai-playerlist__name { color: rgb(255, 214, 120); -unity-font-style: bold; }\n"
            + ".cai-playerlist__more { font-size: 12px; color: rgb(200, 206, 216); -unity-text-align: middle-left; }\n";

        /// <summary>Stat columns shown at most.</summary>
        public int MaxColumns { get; set; } = RbxPlayerList.DefaultMaxColumns;

        /// <summary>Rows shown at most; the rest is one "+N more" line.</summary>
        public int MaxRows { get; set; } = 12;

        /// <summary>The screen's draw order: below mod screens (whose default is 0), so a mod's HUD is never covered.</summary>
        public int SortOrder { get; set; } = -100;

        /// <summary>The USS; replace it to move or restyle the list.</summary>
        public string Uss { get; set; } = DefaultUss;

        /// <summary>Draw the list while no player has a stat (Roblox shows the names alone); off by default.</summary>
        public bool ShowWithoutStats { get; set; }
    }

    /// <summary>
    /// Draws an <see cref="RbxPlayerList"/> as one host-owned screen of the runtime UI (<see cref="IRbxUiBackend"/>, the
    /// host the <c>UxmlService</c> and Roblox GUI screens draw on): a header with the stat names and one row per player,
    /// the local player's highlighted. Switched by <c>StarterGui:SetCoreGuiEnabled(Enum.CoreGuiType.PlayerList, ...)</c>.
    /// Engine-free; the Unity adapter of the port draws it.
    /// </summary>
    /// <remarks>
    /// Once a frame (<see cref="SchedulerPhase.PreRender"/>) it refreshes the model; the markup is rebuilt only when the
    /// number of rows or columns changes, and otherwise only the texts that differ from the last drawn ones are set, so
    /// an unchanged list allocates nothing and writes nothing. Every element ignores the pointer.
    /// </remarks>
    public sealed class RbxPlayerListView : IDisposable
    {
        /// <summary>The host screen id (a world prefix goes in front of it).</summary>
        public const string ScreenId = "coreai.playerlist";

        private const string LocalClass = "cai-playerlist__local";

        private readonly RbxPlayerList _model;
        private readonly IRbxUiBackend _backend;
        private readonly Func<bool> _enabled;
        private readonly Func<RbxPlayer> _localPlayer;
        private readonly RbxPlayerListOptions _options;
        private readonly string _hostId;
        private readonly Action<SchedulerPhase, double> _onPhase;
        private readonly List<IRbxUiElement> _headerCells = new();
        private readonly List<IRbxUiElement> _rowElements = new();
        private readonly List<IRbxUiElement> _rowNames = new();
        private readonly List<IRbxUiElement[]> _rowCells = new();
        private readonly List<string> _drawn = new();
        private ModScheduler _scheduler;
        private IRbxUiScreen _screen;
        private IRbxUiElement _more;
        private int _shapeRows = -1;
        private int _shapeColumns = -1;
        private bool _shapeMore;
        private int _drawnVersion = -1;
        private int _refusedVersion = -1;
        private int _localRow = -1;
        private bool _disposed;

        /// <summary>Creates the view of one world.</summary>
        /// <param name="model">The list data.</param>
        /// <param name="backend">The runtime UI host; null draws nothing (headless).</param>
        /// <param name="scheduler">The world's scheduler, whose PreRender drives the view; null leaves it to <see cref="Update"/>.</param>
        /// <param name="enabled">The PlayerList core GUI switch, read every frame; null is always on.</param>
        /// <param name="localPlayer">The player at this screen, highlighted; may be null.</param>
        /// <param name="options">How the list is drawn; null uses the defaults.</param>
        /// <param name="hostIdPrefix">Prefix of the host screen id, keeping two worlds of one host apart.</param>
        public RbxPlayerListView(RbxPlayerList model, IRbxUiBackend backend, ModScheduler scheduler = null,
            Func<bool> enabled = null, Func<RbxPlayer> localPlayer = null, RbxPlayerListOptions options = null,
            string hostIdPrefix = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _backend = backend;
            _enabled = enabled;
            _localPlayer = localPlayer;
            _options = options ?? new RbxPlayerListOptions();
            _hostId = (hostIdPrefix ?? string.Empty) + ScreenId;
            _onPhase = OnPhase;
            if (scheduler != null)
            {
                _scheduler = scheduler;
                _scheduler.PhaseReached += _onPhase;
            }
        }

        /// <summary>True while the list is on screen.</summary>
        public bool IsShown => _screen != null && !_screen.IsDestroyed && _screen.IsVisible;

        /// <summary>The host screen, or null before the list was first shown.</summary>
        public IRbxUiScreen Screen => _screen;

        /// <summary>The model version last drawn.</summary>
        public int DrawnVersion => _drawnVersion;

        /// <summary>Brings the screen up to date; runs once a frame on its own, and tests and hosts may call it.</summary>
        public void Update()
        {
            if (_disposed || _backend == null)
            {
                return;
            }

            bool changed = _model.Refresh();
            bool wanted = (_enabled == null || _enabled()) && _backend.IsEnabled && _model.RowCount > 0
                          && (_model.HasStats || _options.ShowWithoutStats);
            if (!wanted)
            {
                if (_screen != null && !_screen.IsDestroyed && _screen.IsVisible)
                {
                    _screen.SetVisible(false);
                }

                return;
            }

            if (_screen == null || _screen.IsDestroyed)
            {
                // WHY a refused screen is asked for again only after the list changed: a host that refuses it (its
                // screen limit, an id still held by the world being replaced) would otherwise be asked every frame.
                if (_refusedVersion == _model.Version || !CreateScreen())
                {
                    _refusedVersion = _model.Version;
                    return;
                }

                changed = true;
            }

            if (!_screen.IsVisible)
            {
                _screen.SetVisible(true);
            }

            if (changed || _drawnVersion != _model.Version)
            {
                Draw();
            }

            UpdateLocalRow();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_scheduler != null)
            {
                _scheduler.PhaseReached -= _onPhase;
                _scheduler = null;
            }

            if (_screen != null && !_screen.IsDestroyed)
            {
                _screen.Destroy();
            }

            _screen = null;
        }

        private void OnPhase(SchedulerPhase phase, double deltaSeconds)
        {
            if (phase == SchedulerPhase.PreRender)
            {
                Update();
            }
        }

        private bool CreateScreen()
        {
            Shape(out int rows, out int columns, out bool more);
            RbxUiResult result = _backend.CreateScreen(_hostId, string.Empty, BuildUxml(rows, columns, more), _options.Uss,
                _options.SortOrder, true);
            if (!result.Success || result.Screen == null)
            {
                return false;
            }

            _screen = result.Screen;
            _shapeRows = rows;
            _shapeColumns = columns;
            _shapeMore = more;
            _drawnVersion = -1;
            _localRow = -1;
            CacheHandles();
            return true;
        }

        private void Shape(out int rows, out int columns, out bool more)
        {
            columns = Math.Min(_model.Columns.Count, Math.Max(1, _options.MaxColumns));
            rows = Math.Min(_model.RowCount, Math.Max(1, _options.MaxRows));
            more = _model.RowCount > rows;
        }

        private void Draw()
        {
            Shape(out int rows, out int columns, out bool more);
            if (rows != _shapeRows || columns != _shapeColumns || more != _shapeMore)
            {
                _screen.ReplaceUxml(BuildUxml(rows, columns, more));
                _shapeRows = rows;
                _shapeColumns = columns;
                _shapeMore = more;
                _localRow = -1;
                CacheHandles();
            }

            int slot = 0;
            for (int column = 0; column < columns; column++)
            {
                SetText(_headerCells[column], slot++, _model.Columns[column]);
            }

            for (int row = 0; row < rows; row++)
            {
                SetText(_rowNames[row], slot++, _model.RowName(row));
                IRbxUiElement[] cells = _rowCells[row];
                for (int column = 0; column < columns; column++)
                {
                    SetText(cells[column], slot++, _model.Cell(row, column));
                }
            }

            if (more && _more != null)
            {
                SetText(_more, slot, MoreText(_model.RowCount - rows));
            }

            _drawnVersion = _model.Version;
        }

        private void UpdateLocalRow()
        {
            RbxPlayer local = _localPlayer?.Invoke();
            int row = -1;
            if (local != null)
            {
                for (int index = 0; index < _shapeRows && index < _model.RowCount; index++)
                {
                    if (ReferenceEquals(_model.RowPlayer(index), local))
                    {
                        row = index;
                        break;
                    }
                }
            }

            if (row == _localRow)
            {
                return;
            }

            if (_localRow >= 0 && _localRow < _rowElements.Count)
            {
                _rowElements[_localRow]?.RemoveClass(LocalClass);
            }

            if (row >= 0 && row < _rowElements.Count)
            {
                _rowElements[row]?.AddClass(LocalClass);
            }

            _localRow = row;
        }

        private void SetText(IRbxUiElement element, int slot, string text)
        {
            while (_drawn.Count <= slot)
            {
                _drawn.Add(null);
            }

            if (string.Equals(_drawn[slot], text, StringComparison.Ordinal))
            {
                return;
            }

            _drawn[slot] = text;
            element?.SetText(text);
        }

        private void CacheHandles()
        {
            _drawn.Clear();
            _headerCells.Clear();
            _rowElements.Clear();
            _rowNames.Clear();
            _rowCells.Clear();
            for (int column = 0; column < _shapeColumns; column++)
            {
                _headerCells.Add(_screen.GetElement("header-" + column));
            }

            for (int row = 0; row < _shapeRows; row++)
            {
                _rowElements.Add(_screen.GetElement("row-" + row));
                _rowNames.Add(_screen.GetElement("row-" + row + "-name"));
                IRbxUiElement[] cells = new IRbxUiElement[_shapeColumns];
                for (int column = 0; column < _shapeColumns; column++)
                {
                    cells[column] = _screen.GetElement("row-" + row + "-" + column);
                }

                _rowCells.Add(cells);
            }

            _more = _shapeMore ? _screen.GetElement("more") : null;
        }

        private static string MoreText(int hidden)
        {
            return "+" + hidden.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more";
        }

        /// <summary>The markup of a list of <paramref name="rows"/> rows and <paramref name="columns"/> stat columns.</summary>
        internal static string BuildUxml(int rows, int columns, bool more)
        {
            StringBuilder uxml = new(256 + rows * (96 + columns * 96));
            uxml.Append("<ui:UXML>\n")
                .Append("  <ui:VisualElement name=\"playerlist\" class=\"cai-playerlist\" picking-mode=\"Ignore\">\n")
                .Append("    <ui:VisualElement name=\"header\" class=\"cai-playerlist__row cai-playerlist__header\" ")
                .Append("picking-mode=\"Ignore\">\n")
                .Append("      <ui:Label name=\"header-name\" class=\"cai-playerlist__name\" text=\"\" picking-mode=\"Ignore\" />\n");
            for (int column = 0; column < columns; column++)
            {
                uxml.Append("      <ui:Label name=\"header-").Append(column)
                    .Append("\" class=\"cai-playerlist__cell\" text=\"\" picking-mode=\"Ignore\" />\n");
            }

            uxml.Append("    </ui:VisualElement>\n");
            for (int row = 0; row < rows; row++)
            {
                uxml.Append("    <ui:VisualElement name=\"row-").Append(row)
                    .Append("\" class=\"cai-playerlist__row\" picking-mode=\"Ignore\">\n")
                    .Append("      <ui:Label name=\"row-").Append(row)
                    .Append("-name\" class=\"cai-playerlist__name\" text=\"\" picking-mode=\"Ignore\" />\n");
                for (int column = 0; column < columns; column++)
                {
                    uxml.Append("      <ui:Label name=\"row-").Append(row).Append('-').Append(column)
                        .Append("\" class=\"cai-playerlist__cell\" text=\"\" picking-mode=\"Ignore\" />\n");
                }

                uxml.Append("    </ui:VisualElement>\n");
            }

            if (more)
            {
                uxml.Append("    <ui:Label name=\"more\" class=\"cai-playerlist__more\" text=\"\" picking-mode=\"Ignore\" />\n");
            }

            uxml.Append("  </ui:VisualElement>\n</ui:UXML>\n");
            return uxml.ToString();
        }
    }
}
