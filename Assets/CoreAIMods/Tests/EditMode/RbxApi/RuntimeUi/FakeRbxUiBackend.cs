using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;

namespace CoreAI.Tests.EditMode.RbxApi.RuntimeUi
{
    /// <summary>
    /// An engine-free runtime UI host behind <see cref="IRbxUiBackend"/>: it reads the element tags of a
    /// UXML text (name, class, text and value attributes, nesting), matches simple selectors (type,
    /// <c>.class</c>, <c>#name</c>, descendant and child combinators) and records every write, so the
    /// Lua surface is tested without UI Toolkit. A UXML replace keeps the element objects, values and
    /// API-set text and classes of named elements, as the real host does; unnamed ones detach.
    /// </summary>
    internal sealed class FakeRbxUiBackend : IRbxUiBackend
    {
        private static readonly Regex TagPattern = new(@"<(/?)(?:[A-Za-z]+:)?([A-Za-z]+)((?:\s+[^\s=/>]+\s*=\s*""[^""]*"")*)\s*(/?)>",
            RegexOptions.CultureInvariant);

        private static readonly Regex AttributePattern = new(@"([^\s=/>]+)\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> KnownKinds = new(StringComparer.Ordinal)
        {
            "VisualElement", "Label", "Button", "Toggle", "Slider", "TextField", "ProgressBar", "Image",
            "ScrollView", "ListView"
        };

        private readonly Dictionary<string, FakeScreen> _screens = new(StringComparer.Ordinal);
        private bool _textFocused;
        private bool _pointerOver;

        public event Action<bool> TextInputFocusChanged;

        public event Action<bool> PointerOverUiChanged;

        public bool IsEnabled { get; set; } = true;

        public bool IsTextInputFocused => _textFocused;

        public bool IsPointerOverUi => _pointerOver;

        public Dictionary<string, string> ThemeTokens { get; } = new(StringComparer.Ordinal)
        {
            { "--cai-color-accent", "rgb(80, 200, 255)" }
        };

        /// <summary>Live screens by host id.</summary>
        public IReadOnlyDictionary<string, FakeScreen> Screens => _screens;

        /// <summary>Every screen this backend ever created, destroyed ones included.</summary>
        public List<FakeScreen> Created { get; } = new();

        /// <summary>Event subscribers over every element of every screen ever created.</summary>
        public int SubscriberCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < Created.Count; i++)
                {
                    count += Created[i].SubscriberCount;
                }

                return count;
            }
        }

        public RbxUiResult CreateScreen(string hostId, string owner, string uxml, string uss, int sortOrder,
            bool visible)
        {
            if (!IsEnabled)
            {
                return RbxUiResult.Fail(RbxUiErrors.Disabled, "runtime UI is switched off in this game");
            }

            if (_screens.ContainsKey(hostId))
            {
                return RbxUiResult.Fail(RbxUiErrors.ScreenExists, "a screen with the id '" + hostId + "' already exists");
            }

            FakeScreen screen = new(this, hostId, owner, sortOrder, visible);
            screen.Build(uxml, uss, true);
            _screens[hostId] = screen;
            Created.Add(screen);
            return RbxUiResult.Ok(screen, screen.Diagnostics);
        }

        public int CountElements(string uxml)
        {
            int count = 0;
            foreach (Match match in TagPattern.Matches(uxml ?? string.Empty))
            {
                if (match.Groups[1].Value.Length == 0 && match.Groups[2].Value != "UXML")
                {
                    count++;
                }
            }

            return count;
        }

        public bool IsValidSelector(string selector, out string error)
        {
            if (string.IsNullOrWhiteSpace(selector) || selector.IndexOfAny(new[] { '[', '+', '~', '(' }) >= 0)
            {
                error = "unsupported selector syntax";
                return false;
            }

            error = null;
            return true;
        }

        public string GetThemeToken(string name)
        {
            return ThemeTokens.TryGetValue(name, out string value) ? value : null;
        }

        public FakeScreen Get(string hostId)
        {
            return _screens.TryGetValue(hostId, out FakeScreen screen) ? screen : null;
        }

        public void SetTextFocus(bool focused)
        {
            if (_textFocused == focused)
            {
                return;
            }

            _textFocused = focused;
            TextInputFocusChanged?.Invoke(focused);
        }

        public void SetPointerOver(bool over)
        {
            if (_pointerOver == over)
            {
                return;
            }

            _pointerOver = over;
            PointerOverUiChanged?.Invoke(over);
        }

        internal void Forget(FakeScreen screen)
        {
            if (_screens.TryGetValue(screen.HostId, out FakeScreen current) && ReferenceEquals(current, screen))
            {
                _screens.Remove(screen.HostId);
            }
        }

        internal static bool IsKnownKind(string kind)
        {
            return KnownKinds.Contains(kind);
        }

        internal sealed class FakeScreen : IRbxUiScreen
        {
            private readonly FakeRbxUiBackend _backend;
            private readonly List<RbxUiDiagnostic> _diagnostics = new();
            private readonly List<FakeElement> _authored = new();

            public FakeScreen(FakeRbxUiBackend backend, string hostId, string owner, int sortOrder, bool visible)
            {
                _backend = backend;
                HostId = hostId;
                Owner = owner;
                SortOrder = sortOrder;
                IsVisible = visible;
                RootElement = new FakeElement(this, "VisualElement", hostId, null);
            }

            public event Action Destroyed;

            public event Action Resized;

            public float Width { get; private set; } = 1280f;

            public float Height { get; private set; } = 720f;

            /// <summary>Every element this screen ever created through the API, removed ones included.</summary>
            public List<FakeElement> CreatedElements { get; } = new();

            public string HostId { get; }

            public string Owner { get; }

            public string UxmlText { get; private set; } = string.Empty;

            public string UssText { get; private set; } = string.Empty;

            public int BuildCount { get; private set; }

            public int StyleCount { get; private set; }

            public bool IsDestroyed { get; private set; }

            public bool IsVisible { get; private set; }

            public int SortOrder { get; private set; }

            public int ElementCount => _authored.Count;

            public IReadOnlyList<RbxUiDiagnostic> Diagnostics => _diagnostics.ToArray();

            public FakeElement RootElement { get; }

            public IRbxUiElement Root => IsDestroyed ? null : RootElement;

            public IReadOnlyList<FakeElement> Authored => _authored;

            public int SubscriberCount
            {
                get
                {
                    int count = RootElement.SubscriberCount;
                    for (int i = 0; i < _authored.Count; i++)
                    {
                        count += _authored[i].SubscriberCount;
                    }

                    return count;
                }
            }

            public RbxUiResult ReplaceUxml(string uxml)
            {
                Build(uxml, null, false);
                return RbxUiResult.Ok(this, Diagnostics);
            }

            public RbxUiResult ReplaceUss(string uss)
            {
                _diagnostics.Clear();
                UssText = uss ?? string.Empty;
                StyleCount++;
                ReportUss(UssText);
                return RbxUiResult.Ok(this, Diagnostics);
            }

            public RbxUiResult Replace(string uxml, string uss)
            {
                Build(uxml, uss, true);
                return RbxUiResult.Ok(this, Diagnostics);
            }

            public void SetVisible(bool visible)
            {
                IsVisible = visible;
            }

            public void SetSortOrder(int sortOrder)
            {
                SortOrder = sortOrder;
            }

            public IRbxUiElement Query(string selector)
            {
                for (int i = 0; i < _authored.Count; i++)
                {
                    if (_authored[i].Matches(selector))
                    {
                        return _authored[i];
                    }
                }

                return null;
            }

            public void QueryAll(string selector, List<IRbxUiElement> results)
            {
                for (int i = 0; i < _authored.Count; i++)
                {
                    if (_authored[i].Matches(selector))
                    {
                        results.Add(_authored[i]);
                    }
                }
            }

            public IRbxUiElement GetElement(string name)
            {
                for (int i = 0; i < _authored.Count; i++)
                {
                    if (_authored[i].Name == name)
                    {
                        return _authored[i];
                    }
                }

                return null;
            }

            public IRbxUiElement CreateElement(string kind, IRbxUiElement parent, string name)
            {
                FakeElement parentElement = parent as FakeElement ?? RootElement;
                if (IsDestroyed || !parentElement.IsAlive || !IsKnownKind(kind))
                {
                    return null;
                }

                FakeElement element = new(this, kind, name, parentElement);
                parentElement.Children.Add(element);
                _authored.Add(element);
                CreatedElements.Add(element);
                return element;
            }

            public void Destroy()
            {
                if (IsDestroyed)
                {
                    return;
                }

                IsDestroyed = true;
                RootElement.Kill();
                for (int i = 0; i < _authored.Count; i++)
                {
                    _authored[i].Kill();
                }

                _authored.Clear();
                _backend.Forget(this);
                Action destroyed = Destroyed;
                Destroyed = null;
                destroyed?.Invoke();
            }

            /// <summary>The panel is resized to <paramref name="width"/> by <paramref name="height"/>.</summary>
            public void Resize(float width, float height)
            {
                Width = width;
                Height = height;
                Resized?.Invoke();
            }

            public FakeElement Find(string name)
            {
                return GetElement(name) as FakeElement;
            }

            internal void Remove(FakeElement element)
            {
                element.Parent?.Children.Remove(element);
                RemoveSubtree(element);
            }

            internal void Build(string uxml, string uss, bool replaceUss)
            {
                _diagnostics.Clear();
                UxmlText = uxml ?? string.Empty;
                if (replaceUss)
                {
                    UssText = uss ?? string.Empty;
                    StyleCount++;
                }

                Dictionary<string, FakeElement> named = new(StringComparer.Ordinal);
                for (int i = 0; i < _authored.Count; i++)
                {
                    if (!string.IsNullOrEmpty(_authored[i].Name) && !named.ContainsKey(_authored[i].Name))
                    {
                        named[_authored[i].Name] = _authored[i];
                    }
                }

                List<FakeElement> previous = new(_authored);
                _authored.Clear();
                RootElement.Children.Clear();
                HashSet<FakeElement> reused = new();
                Stack<FakeElement> open = new();
                open.Push(RootElement);
                foreach (Match match in TagPattern.Matches(UxmlText))
                {
                    bool closing = match.Groups[1].Value.Length > 0;
                    string kind = match.Groups[2].Value;
                    bool selfClosing = match.Groups[4].Value.Length > 0;
                    if (kind == "UXML")
                    {
                        continue;
                    }

                    if (closing)
                    {
                        if (open.Count > 1)
                        {
                            open.Pop();
                        }

                        continue;
                    }

                    Dictionary<string, string> attributes = new(StringComparer.Ordinal);
                    foreach (Match attribute in AttributePattern.Matches(match.Groups[3].Value))
                    {
                        attributes[attribute.Groups[1].Value] = attribute.Groups[2].Value;
                    }

                    attributes.TryGetValue("name", out string name);
                    if (!IsKnownKind(kind))
                    {
                        _diagnostics.Add(new RbxUiDiagnostic("UXML_UNKNOWN_ELEMENT", "Warning", "uxml", 1, match.Index + 1,
                            "unknown element '" + kind + "'; a placeholder stands in"));
                    }

                    FakeElement parent = open.Peek();
                    FakeElement element;
                    if (name != null && named.TryGetValue(name, out FakeElement old) && old.Kind == kind && reused.Add(old))
                    {
                        element = old;
                        element.Rebind(parent, attributes);
                    }
                    else
                    {
                        element = new FakeElement(this, kind, name, parent);
                        element.ApplyMarkup(attributes);
                    }

                    parent.Children.Add(element);
                    _authored.Add(element);
                    if (!selfClosing)
                    {
                        open.Push(element);
                    }
                }

                for (int i = 0; i < previous.Count; i++)
                {
                    if (!reused.Contains(previous[i]))
                    {
                        previous[i].Kill();
                    }
                }

                ReportUss(UssText);
                BuildCount++;
            }

            private void ReportUss(string uss)
            {
                if (uss != null && uss.IndexOf("gap:", StringComparison.Ordinal) >= 0)
                {
                    _diagnostics.Add(new RbxUiDiagnostic("USS_UNSUPPORTED_PROPERTY", "Warning", "uss", 1, 1,
                        "'gap' is not supported; use margins"));
                }
            }

            private void RemoveSubtree(FakeElement element)
            {
                _authored.Remove(element);
                element.Kill();
                for (int i = 0; i < element.Children.Count; i++)
                {
                    RemoveSubtree(element.Children[i]);
                }
            }
        }

        internal sealed class FakeElement : IRbxUiElement
        {
            private readonly FakeScreen _screen;
            private readonly HashSet<string> _apiAdded = new(StringComparer.Ordinal);
            private readonly HashSet<string> _apiRemoved = new(StringComparer.Ordinal);
            private Action _clicked;
            private Action _valueChanged;
            private Action _submitted;
            private Action _pointerEntered;
            private Action _pointerExited;
            private Action _focusGained;
            private Action _focusLost;
            private Action _scrollChanged;
            private bool _apiText;

            public FakeElement(FakeScreen screen, string kind, string name, FakeElement parent)
            {
                _screen = screen;
                Kind = kind;
                Name = name;
                Parent = parent;
                Value = DefaultValue(kind);
            }

            public event Action Clicked
            {
                add => _clicked += value;
                remove => _clicked -= value;
            }

            public event Action ValueChanged
            {
                add => _valueChanged += value;
                remove => _valueChanged -= value;
            }

            public event Action Submitted
            {
                add => _submitted += value;
                remove => _submitted -= value;
            }

            public event Action PointerEntered
            {
                add => _pointerEntered += value;
                remove => _pointerEntered -= value;
            }

            public event Action PointerExited
            {
                add => _pointerExited += value;
                remove => _pointerExited -= value;
            }

            public event Action FocusGained
            {
                add => _focusGained += value;
                remove => _focusGained -= value;
            }

            public event Action FocusLost
            {
                add => _focusLost += value;
                remove => _focusLost -= value;
            }

            public event Action ScrollChanged
            {
                add => _scrollChanged += value;
                remove => _scrollChanged -= value;
            }

            public float ScrollOffsetX { get; private set; }

            public float ScrollOffsetY { get; private set; }

            /// <summary>How many scroll offsets the API set (the player's scrolling is not counted).</summary>
            public int ScrollWrites { get; private set; }

            /// <summary>Every typed style written through the per-frame path, last value per property.</summary>
            public Dictionary<RbxUiStyleProperty, RbxUiStyleValue> TypedStyles { get; } = new();

            /// <summary>How many typed style writes reached this element.</summary>
            public int TypedStyleWrites { get; private set; }

            public bool IsAlive { get; private set; } = true;

            public string Name { get; }

            public string Kind { get; }

            public FakeElement Parent { get; private set; }

            IRbxUiElement IRbxUiElement.Parent => Parent;

            public List<FakeElement> Children { get; } = new();

            public HashSet<string> Classes { get; } = new(StringComparer.Ordinal);

            public Dictionary<string, string> Styles { get; } = new(StringComparer.Ordinal);

            public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

            public List<string> Items { get; } = new();

            public string Text { get; private set; }

            public RbxUiValue Value { get; private set; }

            public bool Visible { get; private set; } = true;

            public bool Enabled { get; private set; } = true;

            public int ZOrder { get; private set; }

            public bool Focused { get; private set; }

            public int SubscriberCount => Count(_clicked) + Count(_valueChanged) + Count(_submitted)
                                          + Count(_pointerEntered) + Count(_pointerExited) + Count(_focusGained)
                                          + Count(_focusLost) + Count(_scrollChanged);

            public bool SetStyle(RbxUiStyleProperty property, RbxUiStyleValue value)
            {
                if (!IsAlive || value.Kind == RbxUiStyleValueKind.None)
                {
                    return false;
                }

                TypedStyles[property] = value;
                TypedStyleWrites++;
                return true;
            }

            /// <summary>The last typed value of a property, or a value of kind None.</summary>
            public RbxUiStyleValue Typed(RbxUiStyleProperty property)
            {
                return TypedStyles.TryGetValue(property, out RbxUiStyleValue value) ? value : default;
            }

            public bool SetText(string text)
            {
                if (!IsAlive || !HasText(Kind))
                {
                    return false;
                }

                Text = text;
                _apiText = true;
                if (Kind == "TextField")
                {
                    Value = RbxUiValue.FromText(text);
                }

                return true;
            }

            public bool TryGetValue(out RbxUiValue value)
            {
                value = Value;
                return IsAlive && Value.Kind != RbxUiValueKind.None;
            }

            public bool SetValue(RbxUiValue value)
            {
                if (!IsAlive || Value.Kind == RbxUiValueKind.None)
                {
                    return false;
                }

                if (Value.Kind == RbxUiValueKind.Text)
                {
                    Value = RbxUiValue.FromText(value.ToText());
                    Text = Value.Text;
                    return true;
                }

                if (value.Kind != Value.Kind)
                {
                    return false;
                }

                Value = value;
                return true;
            }

            public bool SetVisible(bool visible)
            {
                Visible = visible;
                return IsAlive;
            }

            public bool SetEnabled(bool enabled)
            {
                Enabled = enabled;
                return IsAlive;
            }

            public bool SetZOrder(int zOrder)
            {
                ZOrder = zOrder;
                return IsAlive;
            }

            public bool SetScrollOffset(float x, float y)
            {
                if (!IsAlive || Kind != "ScrollView" || float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x)
                    || float.IsInfinity(y))
                {
                    return false;
                }

                ScrollOffsetX = x;
                ScrollOffsetY = y;
                ScrollWrites++;
                return true;
            }

            public bool AddClass(string className)
            {
                Classes.Add(className);
                _apiAdded.Add(className);
                _apiRemoved.Remove(className);
                return IsAlive;
            }

            public bool RemoveClass(string className)
            {
                Classes.Remove(className);
                _apiRemoved.Add(className);
                _apiAdded.Remove(className);
                return IsAlive;
            }

            public bool ToggleClass(string className)
            {
                return Classes.Contains(className) ? RemoveClass(className) : AddClass(className);
            }

            public bool HasClass(string className)
            {
                return Classes.Contains(className);
            }

            public void GetClasses(List<string> output)
            {
                output.AddRange(Classes);
            }

            public bool SetStyle(string property, string value)
            {
                if (property == "gap")
                {
                    return false;
                }

                Styles[property] = value;
                return IsAlive;
            }

            public bool ClearStyle(string property)
            {
                return Styles.Remove(property);
            }

            public string GetStyle(string property)
            {
                return Styles.TryGetValue(property, out string value) ? value : null;
            }

            public bool SetAttribute(string name, string value)
            {
                if (name == "no-such-attribute")
                {
                    return false;
                }

                Attributes[name] = value;
                return IsAlive;
            }

            public bool SetItems(IReadOnlyList<string> items)
            {
                if (!IsAlive || Kind != "ListView")
                {
                    return false;
                }

                Items.Clear();
                Items.AddRange(items);
                return true;
            }

            public bool Focus()
            {
                if (IsAlive && !Focused)
                {
                    Focused = true;
                    _focusGained?.Invoke();
                }

                return IsAlive;
            }

            public bool Blur()
            {
                if (IsAlive && Focused)
                {
                    Focused = false;
                    _focusLost?.Invoke();
                }

                return IsAlive;
            }

            public IRbxUiElement Query(string selector)
            {
                for (int i = 0; i < Children.Count; i++)
                {
                    if (Children[i].Matches(selector))
                    {
                        return Children[i];
                    }

                    IRbxUiElement nested = Children[i].Query(selector);
                    if (nested != null)
                    {
                        return nested;
                    }
                }

                return null;
            }

            public IRbxUiElement CreateChild(string kind, string name)
            {
                return _screen.CreateElement(kind, this, name);
            }

            public bool Remove()
            {
                if (!IsAlive || ReferenceEquals(this, _screen.RootElement))
                {
                    return false;
                }

                _screen.Remove(this);
                return true;
            }

            /// <summary>The player clicks the element.</summary>
            public void ClickByPlayer()
            {
                _clicked?.Invoke();
            }

            /// <summary>The player changes the value (a toggle flip, a slider drag, typing).</summary>
            public void ChangeValueByPlayer(RbxUiValue value)
            {
                Value = value;
                if (value.Kind == RbxUiValueKind.Text)
                {
                    Text = value.Text;
                }

                _valueChanged?.Invoke();
            }

            /// <summary>The player presses Enter in the field.</summary>
            public void SubmitByPlayer()
            {
                _submitted?.Invoke();
            }

            /// <summary>The pointer moves onto the element.</summary>
            public void EnterByPlayer()
            {
                _pointerEntered?.Invoke();
            }

            /// <summary>The pointer moves off the element.</summary>
            public void ExitByPlayer()
            {
                _pointerExited?.Invoke();
            }

            /// <summary>The player scrolls a scroll view (wheel, drag or scroll bar) to an offset.</summary>
            public void ScrollByPlayer(float x, float y)
            {
                if (!IsAlive || Kind != "ScrollView")
                {
                    return;
                }

                ScrollOffsetX = x;
                ScrollOffsetY = y;
                _scrollChanged?.Invoke();
            }

            internal bool Matches(string selector)
            {
                string[] parts = selector.Replace(">", " > ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !MatchesCompound(parts[parts.Length - 1]))
                {
                    return false;
                }

                FakeElement current = this;
                bool child = false;
                for (int i = parts.Length - 2; i >= 0; i--)
                {
                    if (parts[i] == ">")
                    {
                        child = true;
                        continue;
                    }

                    FakeElement ancestor = current.Parent;
                    if (child)
                    {
                        if (ancestor == null || ancestor.Parent == null || !ancestor.MatchesCompound(parts[i]))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        while (ancestor != null && ancestor.Parent != null && !ancestor.MatchesCompound(parts[i]))
                        {
                            ancestor = ancestor.Parent;
                        }

                        if (ancestor == null || ancestor.Parent == null)
                        {
                            return false;
                        }
                    }

                    current = ancestor;
                    child = false;
                }

                return true;
            }

            internal void Rebind(FakeElement parent, Dictionary<string, string> attributes)
            {
                Parent = parent;
                Children.Clear();
                RbxUiValue kept = Value;
                bool apiText = _apiText;
                string text = Text;
                Classes.Clear();
                ApplyMarkup(attributes);
                Value = kept;
                if (apiText)
                {
                    Text = text;
                }

                foreach (string added in _apiAdded)
                {
                    Classes.Add(added);
                }

                foreach (string removed in _apiRemoved)
                {
                    Classes.Remove(removed);
                }
            }

            internal void ApplyMarkup(Dictionary<string, string> attributes)
            {
                if (attributes.TryGetValue("class", out string classes))
                {
                    foreach (string className in classes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        Classes.Add(className);
                    }
                }

                if (attributes.TryGetValue("text", out string text))
                {
                    Text = text;
                }

                if (attributes.TryGetValue("value", out string value))
                {
                    switch (Value.Kind)
                    {
                        case RbxUiValueKind.Boolean:
                            Value = RbxUiValue.FromBoolean(value == "true");
                            break;
                        case RbxUiValueKind.Number:
                            Value = RbxUiValue.FromNumber(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
                            break;
                        case RbxUiValueKind.Text:
                            Value = RbxUiValue.FromText(value);
                            Text = value;
                            break;
                    }
                }
            }

            internal void Kill()
            {
                IsAlive = false;
                _clicked = null;
                _valueChanged = null;
                _submitted = null;
                _pointerEntered = null;
                _pointerExited = null;
                _focusGained = null;
                _focusLost = null;
                _scrollChanged = null;
            }

            private bool MatchesCompound(string compound)
            {
                int index = 0;
                string type = ReadIdentifier(compound, ref index);
                if (type.Length > 0 && type != "*" && type != Kind)
                {
                    return false;
                }

                while (index < compound.Length)
                {
                    char marker = compound[index++];
                    string identifier = ReadIdentifier(compound, ref index);
                    if (marker == '.' && !Classes.Contains(identifier))
                    {
                        return false;
                    }

                    if (marker == '#' && Name != identifier)
                    {
                        return false;
                    }
                }

                return true;
            }

            private static string ReadIdentifier(string text, ref int index)
            {
                int start = index;
                while (index < text.Length && text[index] != '.' && text[index] != '#')
                {
                    index++;
                }

                return text.Substring(start, index - start);
            }

            private static RbxUiValue DefaultValue(string kind)
            {
                switch (kind)
                {
                    case "Toggle":
                        return RbxUiValue.FromBoolean(false);
                    case "Slider":
                    case "ProgressBar":
                    case "ListView":
                        return RbxUiValue.FromNumber(0d);
                    case "TextField":
                        return RbxUiValue.FromText(string.Empty);
                    default:
                        return default;
                }
            }

            private static bool HasText(string kind)
            {
                return kind != "VisualElement" && kind != "Image" && kind != "ScrollView" && kind != "ListView";
            }

            private static int Count(Action handlers)
            {
                return handlers == null ? 0 : handlers.GetInvocationList().Length;
            }
        }
    }
}
