using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using UnityEngine.UIElements;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// The default <see cref="IRuntimeUiHost"/>. It keeps every screen under one layer element, which it
    /// parents to its own auto-created panel on first use or to any root a host game hands it through
    /// <see cref="AttachTo"/>. It tracks keyboard focus on that layer to answer "is a text field focused"
    /// and restyles every screen when the theme changes. It has no per-frame work.
    /// </summary>
    public sealed class RuntimeUiHost : IRuntimeUiHost, IDisposable
    {
        /// <summary>Class of the layer element that holds every screen.</summary>
        public const string LayerClass = "cai-runtime-ui-layer";

        /// <summary>Rules the host itself needs: screens fill the layer, placeholders are visible.</summary>
        public const string HostBaseUss =
            ".cai-screen { position: absolute; left: 0; top: 0; right: 0; bottom: 0; }\n" +
            ".cai-ui-placeholder { border-width: 1px; border-color: rgb(255, 90, 90); " +
            "background-color: rgba(255, 90, 90, 0.15); padding: 2px; min-height: 16px; }\n" +
            ".cai-ui-placeholder__label { color: rgb(255, 140, 140); font-size: 11px; }\n";

        private readonly List<RuntimeUiScreen> _screens = new();
        private readonly Dictionary<string, RuntimeUiScreen> _byId = new(StringComparer.Ordinal);
        private readonly List<RuntimeUiScreen> _stack = new();
        private readonly List<RuntimeUiScreen> _sortBuffer = new();
        private readonly IGameLogger _logger;
        private readonly EventCallback<FocusInEvent> _onFocusIn;
        private readonly EventCallback<FocusOutEvent> _onFocusOut;
        private readonly EventCallback<PointerEnterEvent> _onPointerEnter;
        private readonly EventCallback<PointerLeaveEvent> _onPointerLeave;
        private RuntimeUiDocumentSurface _surface;
        private VisualElement _attachedRoot;
        private long _sequence;
        private bool _textFocused;
        private bool _pointerOver;
        private bool _disposed;

        /// <summary>Creates a host.</summary>
        /// <param name="options">Configuration; null uses the defaults.</param>
        /// <param name="logger">Log sink for operation summaries and handler exceptions; may be null.</param>
        public RuntimeUiHost(RuntimeUiHostOptions options = null, IGameLogger logger = null)
        {
            Options = options ?? new RuntimeUiHostOptions();
            _logger = logger;
            Limits = Options.ParseLimits ?? RuntimeUiParseLimits.Default;
            // WHY: the host's own sheet and the default theme are trusted package text; the parse limits
            // guard against oversized mod or AI input and must not truncate them.
            Theme = Options.Theme ?? new RuntimeUiTheme();
            Assets = Options.Assets ?? new RuntimeUiAssetRegistry();
            Templates = Options.Templates ?? new UxmlTemplateRegistry();
            HostBaseSheet = UssStyleSheet.Parse(HostBaseUss);
            Factory = new RuntimeUiElementFactory(Assets);
            Applier = new RuntimeUiStyleApplier(Assets);
            LayerRootElement = new VisualElement { name = "cai-runtime-ui", pickingMode = PickingMode.Ignore };
            LayerRootElement.AddToClassList(LayerClass);
            LayerRootElement.style.position = UnityEngine.UIElements.Position.Absolute;
            LayerRootElement.style.left = 0f;
            LayerRootElement.style.top = 0f;
            LayerRootElement.style.right = 0f;
            LayerRootElement.style.bottom = 0f;
            _onFocusIn = OnLayerFocusIn;
            _onFocusOut = OnLayerFocusOut;
            _onPointerEnter = OnLayerPointerEnter;
            _onPointerLeave = OnLayerPointerLeave;
            LayerRootElement.RegisterCallback(_onFocusIn);
            LayerRootElement.RegisterCallback(_onFocusOut);
            LayerRootElement.RegisterCallback(_onPointerEnter);
            LayerRootElement.RegisterCallback(_onPointerLeave);
            Theme.Changed += OnThemeChanged;
        }

        /// <inheritdoc />
        public event Action<bool> TextInputFocusChanged;

        /// <inheritdoc />
        public event Action<bool> PointerOverUiChanged;

        /// <inheritdoc />
        public event Action<RuntimeUiScreen> ScreenCreated;

        /// <inheritdoc />
        public event Action<RuntimeUiScreen> ScreenDestroyed;

        /// <inheritdoc />
        public bool IsEnabled => !_disposed;

        /// <inheritdoc />
        public RuntimeUiTheme Theme { get; }

        /// <inheritdoc />
        public IUxmlTemplateResolver Templates { get; }

        /// <inheritdoc />
        public IRuntimeUiAssetResolver Assets { get; }

        /// <inheritdoc />
        public IReadOnlyList<RuntimeUiScreen> Screens => _screens;

        /// <inheritdoc />
        public VisualElement LayerRoot => LayerRootElement.parent != null ? LayerRootElement : null;

        /// <inheritdoc />
        public bool IsTextInputFocused => _textFocused;

        /// <inheritdoc />
        public bool IsPointerOverUi => _pointerOver;

        /// <summary>The configuration in effect.</summary>
        public RuntimeUiHostOptions Options { get; }

        /// <summary>The factory screens build with (usable on its own for markup-free elements).</summary>
        public RuntimeUiElementFactory Factory { get; }

        /// <summary>The applier screens style with.</summary>
        public RuntimeUiStyleApplier Applier { get; }

        internal RuntimeUiParseLimits Limits { get; }

        internal UssStyleSheet HostBaseSheet { get; }

        internal VisualElement LayerRootElement { get; }

        /// <inheritdoc />
        public RuntimeUiResult CreateScreen(string id, string uxml, string uss, RuntimeUiScreenOptions options = null)
        {
            if (_disposed)
            {
                return RuntimeUiResult.Fail(RuntimeUiDiagnosticCodes.UiDisabled, "the runtime UI host was disposed");
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                return RuntimeUiResult.Fail(RuntimeUiDiagnosticCodes.UiInvalidArgument, "a screen needs an id");
            }

            string key = id.Trim();
            if (_byId.ContainsKey(key))
            {
                return RuntimeUiResult.Fail(RuntimeUiDiagnosticCodes.UiScreenExists,
                    "a screen with the id '" + key + "' already exists; replace its UXML or USS instead",
                    _byId[key]);
            }

            if (_screens.Count >= Options.MaxScreens)
            {
                return RuntimeUiResult.Fail(RuntimeUiDiagnosticCodes.UiScreenLimit,
                    "the host already holds " + _screens.Count + " screens");
            }

            if (!CheckTextSize(uxml, out RuntimeUiResult refused) || !CheckTextSize(uss, out refused))
            {
                return refused;
            }

            RuntimeUiScreen screen = new(this, key, options, _sequence++);
            RuntimeUiResult built = screen.Replace(uxml, uss, true);
            if (!built.Success)
            {
                screen.DestroyInternal();
                return built;
            }

            _screens.Add(screen);
            _byId[key] = screen;
            LayerRootElement.Add(screen.Container);
            SortScreens();
            EnsureAttached();
            ScreenCreated?.Invoke(screen);
            return built;
        }

        /// <inheritdoc />
        public bool TryGetScreen(string id, out RuntimeUiScreen screen)
        {
            screen = null;
            return id != null && _byId.TryGetValue(id.Trim(), out screen);
        }

        /// <inheritdoc />
        public bool DestroyScreen(string id)
        {
            return TryGetScreen(id, out RuntimeUiScreen screen) && DestroyScreen(screen);
        }

        /// <inheritdoc />
        public int DestroyScreensOwnedBy(string owner)
        {
            if (owner == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = _screens.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_screens[i].Owner, owner, StringComparison.Ordinal) && DestroyScreen(_screens[i]))
                {
                    count++;
                }
            }

            return count;
        }

        /// <inheritdoc />
        public RuntimeUiResult PushScreen(RuntimeUiScreen screen, bool hidePrevious = false)
        {
            if (screen == null || screen.IsDestroyed || !_byId.ContainsKey(screen.Id))
            {
                return RuntimeUiResult.Fail(RuntimeUiDiagnosticCodes.UiScreenNotFound, "the screen is not live");
            }

            _stack.Remove(screen);
            if (hidePrevious && _stack.Count > 0)
            {
                RuntimeUiScreen previous = _stack[_stack.Count - 1];
                if (previous.IsVisible)
                {
                    previous.HiddenByStack = true;
                    previous.SetVisible(false);
                }
            }

            _stack.Add(screen);
            screen.HiddenByStack = false;
            RenumberStack();
            screen.SetVisible(true);
            SortScreens();
            return RuntimeUiResult.Ok(screen, null);
        }

        /// <inheritdoc />
        public RuntimeUiScreen PopScreen()
        {
            if (_stack.Count == 0)
            {
                return null;
            }

            RuntimeUiScreen top = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);
            top.SetStackPosition(-1);
            top.SetVisible(false);
            if (_stack.Count > 0)
            {
                RuntimeUiScreen previous = _stack[_stack.Count - 1];
                if (previous.HiddenByStack)
                {
                    previous.HiddenByStack = false;
                    previous.SetVisible(true);
                }
            }

            RenumberStack();
            SortScreens();
            return top;
        }

        /// <inheritdoc />
        public void AttachTo(VisualElement root)
        {
            if (root == null || _disposed)
            {
                return;
            }

            if (_surface != null)
            {
                RuntimeUiDocumentSurface surface = _surface;
                DetachSurface();
                surface.DestroySurface();
            }

            _attachedRoot = root;
            Reparent(root);
        }

        /// <summary>Takes keyboard focus away from any element of any screen (a controller calls this on Escape).</summary>
        public void ReleaseTextInputFocus()
        {
            ReleaseFocusWithin(LayerRootElement);
        }

        /// <summary>Destroys every screen and the auto-created panel.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            for (int i = _screens.Count - 1; i >= 0; i--)
            {
                DestroyScreen(_screens[i]);
            }

            Theme.Changed -= OnThemeChanged;
            LayerRootElement.UnregisterCallback(_onFocusIn);
            LayerRootElement.UnregisterCallback(_onFocusOut);
            LayerRootElement.UnregisterCallback(_onPointerEnter);
            LayerRootElement.UnregisterCallback(_onPointerLeave);
            LayerRootElement.RemoveFromHierarchy();
            if (_surface != null)
            {
                RuntimeUiDocumentSurface surface = _surface;
                DetachSurface();
                surface.DestroySurface();
            }

            SetTextFocus(false);
            SetPointerOver(false);
            _disposed = true;
        }

        internal bool DestroyScreen(RuntimeUiScreen screen)
        {
            if (screen == null || !_byId.Remove(screen.Id))
            {
                return false;
            }

            _screens.Remove(screen);
            _stack.Remove(screen);
            RenumberStack();
            screen.DestroyInternal();
            RefreshTextFocus();
            ScreenDestroyed?.Invoke(screen);
            return true;
        }

        internal bool CheckTextSize(string text, out RuntimeUiResult refused)
        {
            if (text != null && text.Length > Limits.MaxTextLength)
            {
                refused = RuntimeUiResult.Fail(RuntimeUiDiagnosticCodes.UiTextTooLarge,
                    "the text is " + text.Length + " characters; the host accepts at most " + Limits.MaxTextLength);
                return false;
            }

            refused = null;
            return true;
        }

        internal void SortScreens()
        {
            _sortBuffer.Clear();
            _sortBuffer.AddRange(_screens);
            for (int pass = 0; pass < _sortBuffer.Count; pass++)
            {
                for (int i = _sortBuffer.Count - 1; i > pass; i--)
                {
                    if (CompareScreens(_sortBuffer[i - 1], _sortBuffer[i]) > 0)
                    {
                        RuntimeUiScreen swap = _sortBuffer[i - 1];
                        _sortBuffer[i - 1] = _sortBuffer[i];
                        _sortBuffer[i] = swap;
                    }
                }
            }

            for (int i = 0; i < _sortBuffer.Count; i++)
            {
                VisualElement container = _sortBuffer[i].Container;
                if (container.parent == LayerRootElement)
                {
                    container.BringToFront();
                }
            }

            _sortBuffer.Clear();
        }

        internal void ReleaseFocusWithin(VisualElement container)
        {
            if (container == null)
            {
                return;
            }

            Focusable focused = container.focusController?.focusedElement;
            if (focused is VisualElement element && (element == container || container.Contains(element)))
            {
                element.Blur();
            }

            RefreshTextFocus();
        }

        internal void LogOperation(RuntimeUiScreen screen, string operation, RuntimeUiDiagnosticList diagnostics)
        {
            if (_logger == null || diagnostics.Count == 0)
            {
                return;
            }

            int problems = 0;
            RuntimeUiDiagnostic first = default;
            bool hasFirst = false;
            for (int i = 0; i < diagnostics.Count; i++)
            {
                RuntimeUiDiagnostic diagnostic = diagnostics.Items[i];
                if (diagnostic.Severity == RuntimeUiDiagnosticSeverity.Info)
                {
                    continue;
                }

                problems++;
                if (!hasFirst)
                {
                    first = diagnostic;
                    hasFirst = true;
                }
            }

            if (problems > 0)
            {
                _logger.LogInfo(GameLogFeature.Core,
                    "[RuntimeUi] screen '" + screen.Id + "' " + operation + ": " + problems +
                    " report(s); first: " + first);
            }
        }

        internal void ReportHandlerException(RuntimeUiScreen screen, RuntimeUiElement element, string eventName,
            Exception exception)
        {
            _logger?.LogError(GameLogFeature.Core,
                "[RuntimeUi] a " + eventName + " handler of " + element.Describe() + " on screen '" + screen.Id +
                "' threw: " + exception.GetType().Name + ": " + exception.Message);
        }

        private static int CompareScreens(RuntimeUiScreen a, RuntimeUiScreen b)
        {
            int result = a.SortOrder.CompareTo(b.SortOrder);
            if (result != 0)
            {
                return result;
            }

            result = a.StackIndex.CompareTo(b.StackIndex);
            return result != 0 ? result : a.Sequence.CompareTo(b.Sequence);
        }

        private void RenumberStack()
        {
            for (int i = 0; i < _stack.Count; i++)
            {
                _stack[i].SetStackPosition(i);
            }
        }

        private void EnsureAttached()
        {
            if (LayerRootElement.parent != null || _disposed)
            {
                return;
            }

            if (_attachedRoot != null)
            {
                Reparent(_attachedRoot);
                return;
            }

            if (!Options.AutoCreateSurface)
            {
                return;
            }

            if (_surface == null)
            {
                _surface = RuntimeUiDocumentSurface.Create(Options);
                _surface.RootChanged += OnSurfaceRootChanged;
                _surface.Destroyed += OnSurfaceDestroyed;
            }

            if (_surface.Root != null)
            {
                Reparent(_surface.Root);
            }
        }

        private void Reparent(VisualElement root)
        {
            if (LayerRootElement.parent == root)
            {
                return;
            }

            LayerRootElement.RemoveFromHierarchy();
            root.Add(LayerRootElement);
        }

        private void DetachSurface()
        {
            if (_surface == null)
            {
                return;
            }

            _surface.RootChanged -= OnSurfaceRootChanged;
            _surface.Destroyed -= OnSurfaceDestroyed;
            _surface = null;
            LayerRootElement.RemoveFromHierarchy();
        }

        private void OnSurfaceRootChanged(RuntimeUiDocumentSurface surface)
        {
            if (surface == _surface && surface.Root != null)
            {
                Reparent(surface.Root);
            }
        }

        private void OnSurfaceDestroyed(RuntimeUiDocumentSurface surface)
        {
            if (surface == _surface)
            {
                DetachSurface();
                SetTextFocus(false);
                SetPointerOver(false);
            }
        }

        private void OnThemeChanged(RuntimeUiTheme theme)
        {
            for (int i = 0; i < _screens.Count; i++)
            {
                _screens[i].OnThemeChanged();
            }
        }

        private void OnLayerFocusIn(FocusInEvent evt)
        {
            SetTextFocus(IsTextInput(evt.target as VisualElement));
        }

        private void OnLayerFocusOut(FocusOutEvent evt)
        {
            VisualElement next = evt.relatedTarget as VisualElement;
            SetTextFocus(next != null && LayerRootElement.Contains(next) && IsTextInput(next));
        }

        private void OnLayerPointerEnter(PointerEnterEvent evt)
        {
            // WHY: the layer ignores picking, so it is "entered" only when the pointer reaches a pickable
            // element of some screen, and moving between two such elements never leaves it - no flicker.
            if (evt.target == LayerRootElement)
            {
                SetPointerOver(true);
            }
        }

        private void OnLayerPointerLeave(PointerLeaveEvent evt)
        {
            if (evt.target == LayerRootElement)
            {
                SetPointerOver(false);
            }
        }

        private void SetPointerOver(bool over)
        {
            if (_pointerOver == over)
            {
                return;
            }

            _pointerOver = over;
            PointerOverUiChanged?.Invoke(over);
        }

        private void RefreshTextFocus()
        {
            Focusable focused = LayerRootElement.focusController?.focusedElement;
            VisualElement element = focused as VisualElement;
            SetTextFocus(element != null && LayerRootElement.Contains(element) && IsTextInput(element));
        }

        private bool IsTextInput(VisualElement element)
        {
            for (VisualElement current = element; current != null && current != LayerRootElement;
                 current = current.hierarchy.parent)
            {
                if (current is TextField)
                {
                    return true;
                }
            }

            return false;
        }

        private void SetTextFocus(bool focused)
        {
            if (_textFocused == focused)
            {
                return;
            }

            _textFocused = focused;
            TextInputFocusChanged?.Invoke(focused);
        }
    }
}
