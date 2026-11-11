using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.RuntimeUi;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The Unity side of the <c>UxmlService</c> port: adapts the runtime UI host of part 1
    /// (<see cref="IRuntimeUiHost"/>, its screens and element handles) to <see cref="IRbxUiBackend"/>.
    /// It adds no behaviour of its own; every rule (ownership, budgets, lifetimes) lives in the
    /// engine-free <see cref="RbxUiWorld"/>.
    /// </summary>
    public sealed class RuntimeUiHostBackend : IRbxUiBackend
    {
        private readonly IRuntimeUiHost _host;
        private readonly Dictionary<RuntimeUiScreen, ScreenPort> _screens = new();

        /// <summary>Adapts <paramref name="host"/>.</summary>
        public RuntimeUiHostBackend(IRuntimeUiHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>The adapted host.</summary>
        public IRuntimeUiHost Host => _host;

        /// <inheritdoc />
        public bool IsEnabled => _host.IsEnabled;

        /// <inheritdoc />
        public bool IsTextInputFocused => _host.IsTextInputFocused;

        /// <inheritdoc />
        public bool IsPointerOverUi => _host.IsPointerOverUi;

        /// <inheritdoc />
        public event Action<bool> TextInputFocusChanged
        {
            add => _host.TextInputFocusChanged += value;
            remove => _host.TextInputFocusChanged -= value;
        }

        /// <inheritdoc />
        public event Action<bool> PointerOverUiChanged
        {
            add => _host.PointerOverUiChanged += value;
            remove => _host.PointerOverUiChanged -= value;
        }

        /// <inheritdoc />
        public RbxUiResult CreateScreen(string hostId, string owner, string uxml, string uss, int sortOrder,
            bool visible)
        {
            RuntimeUiResult result = _host.CreateScreen(hostId, uxml, uss, new RuntimeUiScreenOptions
            {
                Owner = owner,
                SortOrder = sortOrder,
                Visible = visible
            });
            if (!result.Success || result.Screen == null)
            {
                return RbxUiResult.Fail(result.ErrorCode, result.Error, Convert(result.Diagnostics));
            }

            return RbxUiResult.Ok(Port(result.Screen), Convert(result.Diagnostics));
        }

        /// <inheritdoc />
        public int CountElements(string uxml)
        {
            if (string.IsNullOrEmpty(uxml))
            {
                return 0;
            }

            return UxmlParser.Parse(uxml, new UxmlParseOptions { TemplateResolver = _host.Templates }).ElementCount;
        }

        /// <inheritdoc />
        public bool IsValidSelector(string selector, out string error)
        {
            return UssComplexSelector.TryParse(selector, 0, 0, out _, out error);
        }

        /// <inheritdoc />
        public string GetThemeToken(string name)
        {
            return _host.Theme != null && _host.Theme.TryGetToken(name, out string value) ? value : null;
        }

        /// <summary>The host element behind a port element of this adapter, or null (tests and host tools).</summary>
        internal static RuntimeUiElement HandleOf(IRbxUiElement port)
        {
            return (port as ElementPort)?.Handle;
        }

        internal static IReadOnlyList<RbxUiDiagnostic> Convert(IReadOnlyList<RuntimeUiDiagnostic> reports)
        {
            if (reports == null || reports.Count == 0)
            {
                return Array.Empty<RbxUiDiagnostic>();
            }

            RbxUiDiagnostic[] converted = new RbxUiDiagnostic[reports.Count];
            for (int i = 0; i < reports.Count; i++)
            {
                RuntimeUiDiagnostic report = reports[i];
                converted[i] = new RbxUiDiagnostic(report.Code, report.Severity.ToString(), SourceName(report.Source),
                    report.Line, report.Column, report.Message);
            }

            return converted;
        }

        /// <summary>The host's longhand for each typed port property, in <see cref="RbxUiStyleProperty"/> order.</summary>
        private static readonly UssPropertyId[] StyleIds =
        {
            UssPropertyId.Display, UssPropertyId.Position, UssPropertyId.Left, UssPropertyId.Top, UssPropertyId.Width,
            UssPropertyId.Height, UssPropertyId.Overflow, UssPropertyId.MarginLeft, UssPropertyId.MarginTop,
            UssPropertyId.MarginRight, UssPropertyId.MarginBottom, UssPropertyId.PaddingLeft, UssPropertyId.PaddingTop,
            UssPropertyId.PaddingRight, UssPropertyId.PaddingBottom, UssPropertyId.BorderLeftWidth,
            UssPropertyId.BorderTopWidth, UssPropertyId.BorderRightWidth, UssPropertyId.BorderBottomWidth,
            UssPropertyId.BorderLeftColor, UssPropertyId.BorderTopColor, UssPropertyId.BorderRightColor,
            UssPropertyId.BorderBottomColor, UssPropertyId.BorderTopLeftRadius, UssPropertyId.BorderTopRightRadius,
            UssPropertyId.BorderBottomRightRadius, UssPropertyId.BorderBottomLeftRadius, UssPropertyId.Color,
            UssPropertyId.FontSize, UssPropertyId.UnityFontStyle, UssPropertyId.UnityTextAlign, UssPropertyId.WhiteSpace,
            UssPropertyId.TextOverflow, UssPropertyId.UnityTextOutlineWidth, UssPropertyId.UnityTextOutlineColor,
            UssPropertyId.BackgroundColor, UssPropertyId.UnityBackgroundImageTintColor, UssPropertyId.Opacity,
            UssPropertyId.Rotate, UssPropertyId.Scale
        };

        /// <summary>Maps a typed port value to the host's typed value; false for an unknown property or no value.</summary>
        internal static bool TryConvert(RbxUiStyleProperty property, RbxUiStyleValue value, out UssPropertyId id,
            out UssValue converted)
        {
            id = default;
            converted = default;
            if (property < 0 || property >= RbxUiStyleProperty.Count || (int)property >= StyleIds.Length)
            {
                return false;
            }

            id = StyleIds[(int)property];
            switch (value.Kind)
            {
                case RbxUiStyleValueKind.Pixels:
                    converted = UssValue.FromLength(value.A);
                    return true;
                case RbxUiStyleValueKind.Percent:
                    converted = UssValue.FromLength(value.A, UssUnit.Percent);
                    return true;
                case RbxUiStyleValueKind.Number:
                    converted = UssValue.FromNumber(value.A);
                    return true;
                case RbxUiStyleValueKind.Color:
                    converted = UssValue.FromColor(value.A, value.B, value.C, value.D);
                    return true;
                case RbxUiStyleValueKind.Keyword:
                    converted = UssValue.FromKeyword(value.Keyword);
                    return true;
                case RbxUiStyleValueKind.Degrees:
                    converted = UssValue.FromAngle(value.A);
                    return true;
                case RbxUiStyleValueKind.Pair:
                    converted = UssValue.FromPair(value.A, UssUnit.None, value.B, UssUnit.None);
                    return true;
                default:
                    return false;
            }
        }

        private ScreenPort Port(RuntimeUiScreen screen)
        {
            if (!_screens.TryGetValue(screen, out ScreenPort port))
            {
                port = new ScreenPort(this, screen);
                _screens[screen] = port;
            }

            return port;
        }

        private void Forget(RuntimeUiScreen screen)
        {
            _screens.Remove(screen);
        }

        private static string SourceName(RuntimeUiSource source)
        {
            switch (source)
            {
                case RuntimeUiSource.Uxml:
                    return "uxml";
                case RuntimeUiSource.Uss:
                    return "uss";
                case RuntimeUiSource.InlineStyle:
                    return "style";
                default:
                    return "api";
            }
        }

        private sealed class ScreenPort : IRbxUiScreen
        {
            private readonly RuntimeUiHostBackend _backend;
            private readonly RuntimeUiScreen _screen;
            private readonly Dictionary<RuntimeUiElement, ElementPort> _elements = new();
            private readonly List<RuntimeUiElement> _scratch = new();
            private readonly List<RuntimeUiElement> _dead = new();
            private readonly Action<RuntimeUiScreen> _onDestroyed;
            private readonly Action<RuntimeUiScreen> _onResized;
            private Action _destroyed;
            private Action _resized;

            public ScreenPort(RuntimeUiHostBackend backend, RuntimeUiScreen screen)
            {
                _backend = backend;
                _screen = screen;
                _onDestroyed = OnDestroyed;
                _onResized = _ => _resized?.Invoke();
                _screen.Destroyed += _onDestroyed;
            }

            public event Action Resized
            {
                add
                {
                    if (value == null)
                    {
                        return;
                    }

                    bool first = _resized == null;
                    _resized += value;
                    if (first && !_screen.IsDestroyed)
                    {
                        _screen.Resized += _onResized;
                    }
                }
                remove
                {
                    if (value == null || _resized == null)
                    {
                        return;
                    }

                    _resized -= value;
                    if (_resized == null)
                    {
                        _screen.Resized -= _onResized;
                    }
                }
            }

            public float Width => _screen.Width;

            public float Height => _screen.Height;

            public event Action Destroyed
            {
                add => _destroyed += value;
                remove => _destroyed -= value;
            }

            public bool IsDestroyed => _screen.IsDestroyed;

            public bool IsVisible => _screen.IsVisible;

            public int SortOrder => _screen.SortOrder;

            public int ElementCount => _screen.IsDestroyed ? 0 : _screen.ElementCount;

            public IReadOnlyList<RbxUiDiagnostic> Diagnostics => Convert(_screen.Diagnostics);

            public IRbxUiElement Root => _screen.IsDestroyed ? null : Element(_screen.RootElement);

            public RbxUiResult ReplaceUxml(string uxml)
            {
                return AfterRebuild(_screen.ReplaceUxml(uxml));
            }

            public RbxUiResult ReplaceUss(string uss)
            {
                RuntimeUiResult result = _screen.ReplaceUss(uss);
                return result.Success
                    ? RbxUiResult.Ok(this, Convert(result.Diagnostics))
                    : RbxUiResult.Fail(result.ErrorCode, result.Error, Convert(result.Diagnostics));
            }

            public RbxUiResult Replace(string uxml, string uss)
            {
                return AfterRebuild(_screen.Replace(uxml, uss));
            }

            public void SetVisible(bool visible)
            {
                if (visible)
                {
                    _screen.Show();
                }
                else
                {
                    _screen.Hide();
                }
            }

            public void SetSortOrder(int sortOrder)
            {
                _screen.SetSortOrder(sortOrder);
            }

            public IRbxUiElement Query(string selector)
            {
                return Element(_screen.Query(selector));
            }

            public void QueryAll(string selector, List<IRbxUiElement> results)
            {
                _scratch.Clear();
                _screen.QueryAll(selector, _scratch);
                for (int i = 0; i < _scratch.Count; i++)
                {
                    results.Add(Element(_scratch[i]));
                }

                _scratch.Clear();
            }

            public IRbxUiElement GetElement(string name)
            {
                return Element(_screen.GetElement(name));
            }

            public IRbxUiElement CreateElement(string kind, IRbxUiElement parent, string name)
            {
                if (!UxmlElementCatalog.TryGetKind(kind, out RuntimeUiElementKind elementKind))
                {
                    return null;
                }

                RuntimeUiElement parentHandle = (parent as ElementPort)?.Handle;
                return Element(_screen.CreateElement(elementKind, parentHandle, name));
            }

            public void Destroy()
            {
                _screen.Destroy();
            }

            public ElementPort Element(RuntimeUiElement handle)
            {
                if (handle == null)
                {
                    return null;
                }

                if (!_elements.TryGetValue(handle, out ElementPort port))
                {
                    port = new ElementPort(this, handle);
                    _elements[handle] = port;
                }

                return port;
            }

            private RbxUiResult AfterRebuild(RuntimeUiResult result)
            {
                _dead.Clear();
                foreach (KeyValuePair<RuntimeUiElement, ElementPort> pair in _elements)
                {
                    if (!pair.Key.IsAlive)
                    {
                        _dead.Add(pair.Key);
                    }
                }

                for (int i = 0; i < _dead.Count; i++)
                {
                    _elements[_dead[i]].Release();
                    _elements.Remove(_dead[i]);
                }

                _dead.Clear();
                return result.Success
                    ? RbxUiResult.Ok(this, Convert(result.Diagnostics))
                    : RbxUiResult.Fail(result.ErrorCode, result.Error, Convert(result.Diagnostics));
            }

            private void OnDestroyed(RuntimeUiScreen screen)
            {
                _screen.Destroyed -= _onDestroyed;
                if (_resized != null)
                {
                    _screen.Resized -= _onResized;
                    _resized = null;
                }
                foreach (ElementPort element in _elements.Values)
                {
                    element.Release();
                }

                _elements.Clear();
                _backend.Forget(_screen);
                Action destroyed = _destroyed;
                _destroyed = null;
                destroyed?.Invoke();
            }
        }

        private sealed class ElementPort : IRbxUiElement
        {
            private readonly ScreenPort _screen;
            private readonly Action<RuntimeUiElement> _onClicked;
            private readonly Action<RuntimeUiElement> _onValueChanged;
            private readonly Action<RuntimeUiElement> _onSubmitted;
            private readonly Action<RuntimeUiElement> _onPointerEntered;
            private readonly Action<RuntimeUiElement> _onPointerExited;
            private readonly Action<RuntimeUiElement> _onFocusGained;
            private readonly Action<RuntimeUiElement> _onFocusLost;
            private readonly Action<RuntimeUiElement> _onScrollChanged;
            private Action _clicked;
            private Action _valueChanged;
            private Action _submitted;
            private Action _pointerEntered;
            private Action _pointerExited;
            private Action _focusGained;
            private Action _focusLost;
            private Action _scrollChanged;

            public ElementPort(ScreenPort screen, RuntimeUiElement handle)
            {
                _screen = screen;
                Handle = handle;
                _onClicked = _ => _clicked?.Invoke();
                _onValueChanged = _ => _valueChanged?.Invoke();
                _onSubmitted = _ => _submitted?.Invoke();
                _onPointerEntered = _ => _pointerEntered?.Invoke();
                _onPointerExited = _ => _pointerExited?.Invoke();
                _onFocusGained = _ => _focusGained?.Invoke();
                _onFocusLost = _ => _focusLost?.Invoke();
                _onScrollChanged = _ => _scrollChanged?.Invoke();
            }

            public RuntimeUiElement Handle { get; }

            public event Action Clicked
            {
                add => Subscribe(ref _clicked, value, h => Handle.Clicked += h, _onClicked);
                remove => Unsubscribe(ref _clicked, value, h => Handle.Clicked -= h, _onClicked);
            }

            public event Action ValueChanged
            {
                add => Subscribe(ref _valueChanged, value, h => Handle.ValueChanged += h, _onValueChanged);
                remove => Unsubscribe(ref _valueChanged, value, h => Handle.ValueChanged -= h, _onValueChanged);
            }

            public event Action Submitted
            {
                add => Subscribe(ref _submitted, value, h => Handle.Submitted += h, _onSubmitted);
                remove => Unsubscribe(ref _submitted, value, h => Handle.Submitted -= h, _onSubmitted);
            }

            public event Action PointerEntered
            {
                add => Subscribe(ref _pointerEntered, value, h => Handle.PointerEntered += h, _onPointerEntered);
                remove => Unsubscribe(ref _pointerEntered, value, h => Handle.PointerEntered -= h, _onPointerEntered);
            }

            public event Action PointerExited
            {
                add => Subscribe(ref _pointerExited, value, h => Handle.PointerExited += h, _onPointerExited);
                remove => Unsubscribe(ref _pointerExited, value, h => Handle.PointerExited -= h, _onPointerExited);
            }

            public event Action FocusGained
            {
                add => Subscribe(ref _focusGained, value, h => Handle.FocusGained += h, _onFocusGained);
                remove => Unsubscribe(ref _focusGained, value, h => Handle.FocusGained -= h, _onFocusGained);
            }

            public event Action FocusLost
            {
                add => Subscribe(ref _focusLost, value, h => Handle.FocusLost += h, _onFocusLost);
                remove => Unsubscribe(ref _focusLost, value, h => Handle.FocusLost -= h, _onFocusLost);
            }

            public event Action ScrollChanged
            {
                add => Subscribe(ref _scrollChanged, value, h => Handle.ScrollChanged += h, _onScrollChanged);
                remove => Unsubscribe(ref _scrollChanged, value, h => Handle.ScrollChanged -= h, _onScrollChanged);
            }

            public float ScrollOffsetX => Handle.ScrollOffset.x;

            public float ScrollOffsetY => Handle.ScrollOffset.y;

            public bool IsAlive => Handle.IsAlive;

            public string Name => Handle.Name;

            public string Kind => Handle.TypeName;

            public IRbxUiElement Parent => _screen.Element(Handle.Parent);

            public string Text => Handle.Text;

            public bool Visible => Handle.Visible;

            public bool Enabled => Handle.Enabled;

            public int ZOrder => Handle.ZOrder;

            public bool SetText(string text)
            {
                return Handle.SetText(text);
            }

            public bool TryGetValue(out RbxUiValue value)
            {
                if (Handle.TryGetBool(out bool flag))
                {
                    value = RbxUiValue.FromBoolean(flag);
                    return true;
                }

                if (Handle.TryGetNumber(out float number))
                {
                    value = RbxUiValue.FromNumber(number);
                    return true;
                }

                if (Handle.TryGetValue(out string text))
                {
                    value = RbxUiValue.FromText(text);
                    return true;
                }

                value = default;
                return false;
            }

            public bool SetValue(RbxUiValue value)
            {
                switch (value.Kind)
                {
                    case RbxUiValueKind.Boolean:
                        return Handle.SetBool(value.Boolean) || Handle.Kind == RuntimeUiElementKind.TextField
                            && Handle.SetValue(value.ToText());
                    case RbxUiValueKind.Number:
                        return Handle.SetNumber((float)value.Number) || Handle.Kind == RuntimeUiElementKind.TextField
                            && Handle.SetValue(value.ToText());
                    case RbxUiValueKind.Text:
                        return Handle.SetValue(value.Text);
                    default:
                        return false;
                }
            }

            public bool SetVisible(bool visible)
            {
                if (!Handle.IsAlive)
                {
                    return false;
                }

                Handle.Visible = visible;
                return true;
            }

            public bool SetEnabled(bool enabled)
            {
                return Handle.SetEnabled(enabled);
            }

            public bool SetZOrder(int zOrder)
            {
                return Handle.SetZOrder(zOrder);
            }

            public bool SetScrollOffset(float x, float y)
            {
                return Handle.SetScrollOffset(new UnityEngine.Vector2(x, y));
            }

            public bool AddClass(string className)
            {
                return Handle.AddClass(className);
            }

            public bool RemoveClass(string className)
            {
                return Handle.RemoveClass(className);
            }

            public bool ToggleClass(string className)
            {
                return Handle.ToggleClass(className);
            }

            public bool HasClass(string className)
            {
                return Handle.HasClass(className);
            }

            public void GetClasses(List<string> output)
            {
                Handle.GetClasses(output);
            }

            public bool SetStyle(string property, string value)
            {
                return Handle.SetStyle(property, value);
            }

            public bool SetStyle(RbxUiStyleProperty property, RbxUiStyleValue value)
            {
                return TryConvert(property, value, out UssPropertyId id, out UssValue converted)
                       && Handle.SetStyle(id, converted);
            }

            public bool ClearStyle(string property)
            {
                return Handle.ClearStyle(property);
            }

            public string GetStyle(string property)
            {
                if (!UssPropertyCatalog.TryGetLonghand(property, out UssPropertySpec spec))
                {
                    return null;
                }

                return Handle.TryGetResolvedStyle(spec.Id, out UssValue value) && value.HasValue
                    ? value.ToUssText()
                    : null;
            }

            public bool SetAttribute(string name, string value)
            {
                return Handle.SetAttribute(name, value);
            }

            public bool SetItems(IReadOnlyList<string> items)
            {
                return Handle.SetItems(items);
            }

            public bool Focus()
            {
                return Handle.Focus();
            }

            public bool Blur()
            {
                return Handle.Blur();
            }

            public IRbxUiElement Query(string selector)
            {
                return _screen.Element(Handle.Query(selector));
            }

            public IRbxUiElement CreateChild(string kind, string name)
            {
                return _screen.CreateElement(kind, this, name);
            }

            public bool Remove()
            {
                return Handle.Remove();
            }

            public void Release()
            {
                if (_clicked != null)
                {
                    Handle.Clicked -= _onClicked;
                    _clicked = null;
                }

                if (_valueChanged != null)
                {
                    Handle.ValueChanged -= _onValueChanged;
                    _valueChanged = null;
                }

                if (_submitted != null)
                {
                    Handle.Submitted -= _onSubmitted;
                    _submitted = null;
                }

                if (_pointerEntered != null)
                {
                    Handle.PointerEntered -= _onPointerEntered;
                    _pointerEntered = null;
                }

                if (_pointerExited != null)
                {
                    Handle.PointerExited -= _onPointerExited;
                    _pointerExited = null;
                }

                if (_focusGained != null)
                {
                    Handle.FocusGained -= _onFocusGained;
                    _focusGained = null;
                }

                if (_focusLost != null)
                {
                    Handle.FocusLost -= _onFocusLost;
                    _focusLost = null;
                }

                if (_scrollChanged != null)
                {
                    Handle.ScrollChanged -= _onScrollChanged;
                    _scrollChanged = null;
                }
            }

            private static void Subscribe(ref Action field, Action handler, Action<Action<RuntimeUiElement>> attach,
                Action<RuntimeUiElement> relay)
            {
                if (handler == null)
                {
                    return;
                }

                bool first = field == null;
                field += handler;
                if (first)
                {
                    attach(relay);
                }
            }

            private static void Unsubscribe(ref Action field, Action handler,
                Action<Action<RuntimeUiElement>> detach, Action<RuntimeUiElement> relay)
            {
                if (handler == null || field == null)
                {
                    return;
                }

                field -= handler;
                if (field == null)
                {
                    detach(relay);
                }
            }
        }
    }
}
