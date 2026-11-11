using System;
using System.Globalization;
using CoreAI.Ai.Logging;
using CoreAI.RuntimeUi;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM && (COREAI_HAS_INPUT_SYSTEM || UNITY_6000_7_OR_NEWER)
using UnityEngine.InputSystem;
#endif

namespace CoreAI.Presentation
{
    /// <summary>
    /// The UI Toolkit view of the in-game mod console, built from UXML and USS text on the runtime UI host
    /// (<see cref="IRuntimeUiHost"/>), so it works the same in the editor, a built player and WebGL. A band across the
    /// top of the screen (at most 900 panel pixels wide, 42 percent high) with buttons only: level filter, mod filter,
    /// older, newest, clear and close, then a fixed pool of row labels, newest at the bottom. It has no text field, so
    /// it never takes keyboard focus from the chat or a mod's HUD; pointer-over is reported by the host like any
    /// screen, so a click on the console never reaches the world. Nothing exists until it is first opened; while
    /// closed nothing runs; while open a refresh redraws only when the model changed.
    /// </summary>
    public sealed class CoreAiModConsoleView : IDisposable
    {
        /// <summary>The runtime UI screen id.</summary>
        public const string ScreenId = "coreai.mod-console";

        /// <summary>Default number of row labels.</summary>
        public const int DefaultVisibleRows = 40;

        /// <summary>Default sort order: above a mod's HUD screens.</summary>
        public const int DefaultSortOrder = 900;

        /// <summary>Rows one press of Older or Newer moves.</summary>
        public const int ScrollStep = 20;

        private const string Uss =
            ".mc-panel { position: absolute; left: 0; right: 0; top: 0; height: 42%; max-width: 900px; " +
            "background-color: rgba(12, 14, 18, 0.92); border-bottom-width: 1px; border-color: rgb(70, 80, 95); " +
            "padding: 6px; }\n" +
            ".mc-bar { flex-direction: row; flex-wrap: wrap; align-items: center; flex-shrink: 0; }\n" +
            ".mc-title { color: rgb(232, 236, 240); font-size: 15px; -unity-font-style: bold; margin-right: 8px; }\n" +
            ".mc-btn { font-size: 13px; height: 26px; margin: 2px; padding-left: 8px; padding-right: 8px; }\n" +
            ".mc-status { color: rgb(150, 160, 170); font-size: 12px; flex-shrink: 0; margin-bottom: 2px; }\n" +
            ".mc-rows { flex-grow: 1; overflow: hidden; justify-content: flex-end; }\n" +
            ".mc-row { color: rgb(222, 226, 230); font-size: 13px; margin: 0; padding: 0; }\n" +
            ".mc-row--warn { color: rgb(255, 205, 90); }\n" +
            ".mc-row--error { color: rgb(255, 112, 100); }\n" +
            ".mc-row--repair { color: rgb(120, 200, 255); }\n" +
            ".mc-row--lifecycle { color: rgb(140, 150, 160); }\n";

        private static readonly string[] KindClasses =
        {
            "mc-row--print", "mc-row--warn", "mc-row--error", "mc-row--repair", "mc-row--lifecycle"
        };

        private readonly IRuntimeUiHost _host;
        private readonly LuaModConsoleModel _model;
        private readonly int _sortOrder;
        private readonly LuaModConsoleRow[] _rowsBuffer;
        private readonly RuntimeUiElement[] _rowLabels;
        private readonly string[] _shownText;
        private readonly int[] _shownKind;
        private readonly bool[] _shownVisible;
        private RuntimeUiScreen _screen;
        private RuntimeUiElement _levelButton;
        private RuntimeUiElement _modButton;
        private RuntimeUiElement _status;
        private int _drawnVersion = -1;
        private bool _shown;
        private bool _disposed;

        /// <param name="host">The runtime UI host the console draws on.</param>
        /// <param name="model">What the console shows.</param>
        /// <param name="visibleRows">Row labels; clamped to 5-80.</param>
        /// <param name="sortOrder">Screen order on the host.</param>
        public CoreAiModConsoleView(IRuntimeUiHost host, LuaModConsoleModel model,
            int visibleRows = DefaultVisibleRows, int sortOrder = DefaultSortOrder)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _model = model ?? throw new ArgumentNullException(nameof(model));
            int rows = Math.Max(5, Math.Min(80, visibleRows));
            _sortOrder = sortOrder;
            _rowsBuffer = new LuaModConsoleRow[rows];
            _rowLabels = new RuntimeUiElement[rows];
            _shownText = new string[rows];
            _shownKind = new int[rows];
            _shownVisible = new bool[rows];
        }

        /// <summary>True once the screen exists.</summary>
        public bool IsBuilt => _screen != null && !_screen.IsDestroyed;

        /// <summary>Why the screen could not be built; empty when it was or was not tried.</summary>
        public string LastError { get; private set; } = "";

        /// <summary>Shows, hides and redraws to match the model. Call once per frame; cheap while nothing changed.</summary>
        public void Refresh()
        {
            if (_disposed)
            {
                return;
            }

            bool open = _model.IsOpen;
            if (!open)
            {
                if (_shown && IsBuilt)
                {
                    _screen.Hide();
                }

                _shown = false;
                return;
            }

            if (!IsBuilt && !Build())
            {
                return;
            }

            if (!_shown)
            {
                _screen.Show();
                _shown = true;
                _drawnVersion = -1;
            }

            int version = _model.Version;
            if (version == _drawnVersion)
            {
                return;
            }

            _drawnVersion = version;
            Draw();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (IsBuilt)
            {
                _screen.Destroy();
            }

            _screen = null;
        }

        private bool Build()
        {
            RuntimeUiResult result = _host.CreateScreen(ScreenId, BuildUxml(_rowLabels.Length), Uss,
                new RuntimeUiScreenOptions { SortOrder = _sortOrder, Visible = false, Owner = ScreenId });
            if (!result.Success || result.Screen == null)
            {
                LastError = result.ToString();
                _model.SetOpen(false);
                return false;
            }

            _screen = result.Screen;
            _levelButton = _screen.GetElement("level");
            _modButton = _screen.GetElement("mod");
            _status = _screen.GetElement("status");
            Wire("level", () => _model.CycleLevelFilter());
            Wire("mod", () => _model.CycleModFilter());
            Wire("older", () => _model.Scroll(ScrollStep));
            Wire("newest", () => _model.ScrollToNewest());
            Wire("clear", () => _model.Clear());
            Wire("close", () => _model.SetOpen(false));
            for (int index = 0; index < _rowLabels.Length; index++)
            {
                _rowLabels[index] = _screen.GetElement("row" + index.ToString(CultureInfo.InvariantCulture));
                _shownText[index] = null;
                _shownKind[index] = -1;
                _shownVisible[index] = true;
            }

            LastError = "";
            return true;
        }

        private void Wire(string name, Action action)
        {
            RuntimeUiElement element = _screen.GetElement(name);
            if (element != null)
            {
                element.Clicked += _ => action();
            }
        }

        private void Draw()
        {
            int written = _model.CopyVisible(_rowsBuffer, out int matching);
            _levelButton?.SetText(_model.LevelFilterLabel);
            _modButton?.SetText(_model.ModFilterLabel);
            int back = _model.ScrollBack;
            _status?.SetText(matching.ToString(CultureInfo.InvariantCulture) + " lines" +
                             (back > 0 ? ", " + back.ToString(CultureInfo.InvariantCulture) + " newer hidden (Newest)" : "") +
                             (_model.Dropped > 0 ? ", oldest dropped" : ""));
            for (int index = 0; index < _rowLabels.Length; index++)
            {
                RuntimeUiElement label = _rowLabels[index];
                if (label == null)
                {
                    continue;
                }

                bool visible = index < written;
                if (visible != _shownVisible[index])
                {
                    label.Visible = visible;
                    _shownVisible[index] = visible;
                }

                if (!visible)
                {
                    continue;
                }

                LuaModConsoleRow row = _rowsBuffer[index];
                if (!ReferenceEquals(row.Text, _shownText[index]))
                {
                    label.SetText(row.Text);
                    _shownText[index] = row.Text;
                }

                int kind = (int)row.Kind;
                if (kind != _shownKind[index])
                {
                    if (_shownKind[index] >= 0)
                    {
                        label.RemoveClass(KindClasses[_shownKind[index]]);
                    }

                    label.AddClass(KindClasses[kind]);
                    _shownKind[index] = kind;
                }
            }
        }

        private static string BuildUxml(int rows)
        {
            System.Text.StringBuilder uxml = new();
            uxml.Append("<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">")
                .Append("<ui:VisualElement name=\"console\" class=\"mc-panel\">")
                .Append("<ui:VisualElement name=\"bar\" class=\"mc-bar\">")
                .Append("<ui:Label name=\"title\" class=\"mc-title\" text=\"Mod console\"/>")
                .Append("<ui:Button name=\"level\" class=\"mc-btn\" text=\"All levels\"/>")
                .Append("<ui:Button name=\"mod\" class=\"mc-btn\" text=\"All mods\"/>")
                .Append("<ui:Button name=\"older\" class=\"mc-btn\" text=\"Older\"/>")
                .Append("<ui:Button name=\"newest\" class=\"mc-btn\" text=\"Newest\"/>")
                .Append("<ui:Button name=\"clear\" class=\"mc-btn\" text=\"Clear\"/>")
                .Append("<ui:Button name=\"close\" class=\"mc-btn\" text=\"Close\"/>")
                .Append("</ui:VisualElement>")
                .Append("<ui:Label name=\"status\" class=\"mc-status\" text=\"\"/>")
                .Append("<ui:VisualElement name=\"rows\" class=\"mc-rows\">");
            for (int index = 0; index < rows; index++)
            {
                uxml.Append("<ui:Label name=\"row").Append(index.ToString(CultureInfo.InvariantCulture))
                    .Append("\" class=\"mc-row\" text=\"\"/>");
            }

            uxml.Append("</ui:VisualElement></ui:VisualElement></ui:UXML>");
            return uxml.ToString();
        }
    }

    /// <summary>
    /// The console's toggle key, polled without allocating: the key is mapped to the Input System once, and the legacy
    /// input manager is the fallback, as for the chat overlay's keys.
    /// </summary>
    public sealed class CoreAiModConsoleKey
    {
        private readonly KeyCode _key;
#if ENABLE_INPUT_SYSTEM && (COREAI_HAS_INPUT_SYSTEM || UNITY_6000_7_OR_NEWER)
        private readonly bool _mapped;
        private readonly Key _inputSystemKey;
#endif

        /// <summary>Polls <paramref name="key"/>; <see cref="KeyCode.None"/> never fires.</summary>
        public CoreAiModConsoleKey(KeyCode key)
        {
            _key = key;
#if ENABLE_INPUT_SYSTEM && (COREAI_HAS_INPUT_SYSTEM || UNITY_6000_7_OR_NEWER)
            _mapped = CoreAI.ChatOverlay.ChatOverlayHotkeys.TryMapToInputSystemKey(key, out _inputSystemKey);
#endif
        }

        /// <summary>True in the frame the key went down with no Ctrl, Alt or Command held.</summary>
        public bool WasPressedThisFrame()
        {
            if (_key == KeyCode.None)
            {
                return false;
            }

#if ENABLE_INPUT_SYSTEM && (COREAI_HAS_INPUT_SYSTEM || UNITY_6000_7_OR_NEWER)
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && _mapped)
            {
                return keyboard[_inputSystemKey].wasPressedThisFrame && !keyboard.ctrlKey.isPressed &&
                       !keyboard.altKey.isPressed && !keyboard.leftMetaKey.isPressed && !keyboard.rightMetaKey.isPressed;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            try
            {
                return Input.GetKeyDown(_key);
            }
            catch (InvalidOperationException)
            {
                // WHY: the legacy manager throws while the project is switched to the Input System only.
                return false;
            }
#else
            return false;
#endif
        }
    }
}
