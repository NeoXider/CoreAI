using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// A stable handle on one element of a runtime screen: read and write its text, value, visibility,
    /// classes and single style properties, arrange it, and listen to clicks, value changes and submits.
    /// A handle on a named element survives a UXML replace: it rebinds to the new element with the same
    /// name (and the same position among equal names), keeps its subscribers and replays what was set
    /// through it. A handle whose element vanished is detached: <see cref="IsAlive"/> turns false, its
    /// operations return false and its events stop.
    /// </summary>
    public sealed class RuntimeUiElement
    {
        private readonly List<KeyValuePair<string, string>> _attributeOverrides = new();
        private readonly List<KeyValuePair<string, bool>> _classLog = new();
        private readonly EventCallback<ClickEvent> _onClick;
        private readonly Action _onButtonClicked;
        private readonly EventCallback<ChangeEvent<bool>> _onBoolChanged;
        private readonly EventCallback<ChangeEvent<float>> _onFloatChanged;
        private readonly EventCallback<ChangeEvent<string>> _onStringChanged;
        private readonly Action<IEnumerable<int>> _onSelectionChanged;
        private readonly EventCallback<KeyDownEvent> _onKeyDown;
        private readonly EventCallback<PointerEnterEvent> _onPointerEnter;
        private readonly EventCallback<PointerLeaveEvent> _onPointerLeave;
        private readonly EventCallback<FocusInEvent> _onFocusIn;
        private readonly EventCallback<FocusOutEvent> _onFocusOut;
        private readonly Action<float> _onScrollerValueChanged;
        private readonly EventCallback<KeyDownEvent> _onTypingKeyDown;
        private readonly OpeningKeystrokeFilter _openingKeystroke = new();
        private VisualElement _typingKeyDownOn;

        private Action<RuntimeUiElement> _clicked;
        private Action<RuntimeUiElement> _valueChanged;
        private Action<RuntimeUiElement> _submitted;
        private Action<RuntimeUiElement> _pointerEntered;
        private Action<RuntimeUiElement> _pointerExited;
        private Action<RuntimeUiElement> _focusGained;
        private Action<RuntimeUiElement> _focusLost;
        private Action<RuntimeUiElement> _scrollChanged;
        private EventGroups _registered;
        private VisualElement _registeredOn;
        private string _textOverride;
        private bool _hasTextOverride;
        private bool? _enabledOverride;
        private List<string> _listItems;
        private int _zOrder;
        private bool _hasZOrder;
        private Background _image;
        private bool _hasImage;
        private bool _focused;
        private Vector2 _knownScroll;
        private bool _applyingScroll;

        internal RuntimeUiElement(RuntimeUiScreen screen, RuntimeUiNodeRecord record)
        {
            Screen = screen;
            Record = record;
            Kind = record.Kind;
            BindName = string.IsNullOrEmpty(record.Name) ? null : record.Name;
            BindOccurrence = record.NameOccurrence;
            _onClick = OnClick;
            _onButtonClicked = OnButtonClicked;
            _onBoolChanged = OnBoolChanged;
            _onFloatChanged = OnFloatChanged;
            _onStringChanged = OnStringChanged;
            _onSelectionChanged = OnSelectionChanged;
            _onKeyDown = OnKeyDown;
            _onPointerEnter = OnPointerEnter;
            _onPointerLeave = OnPointerLeave;
            _onFocusIn = OnFocusIn;
            _onFocusOut = OnFocusOut;
            _onScrollerValueChanged = OnScrollerValueChanged;
            _onTypingKeyDown = OnTypingKeyDown;
        }

        [Flags]
        private enum EventGroups
        {
            None = 0,
            Click = 1,
            Value = 2,
            Submit = 4,
            Pointer = 8,
            Focus = 16,
            Scroll = 32
        }

        /// <summary>Raised when the element is clicked (a button also on keyboard or gamepad submit).</summary>
        public event Action<RuntimeUiElement> Clicked
        {
            add => Subscribe(ref _clicked, value);
            remove => Unsubscribe(ref _clicked, value);
        }

        /// <summary>Raised when the player changes the value (toggle, slider, text field, list selection).</summary>
        public event Action<RuntimeUiElement> ValueChanged
        {
            add => Subscribe(ref _valueChanged, value);
            remove => Unsubscribe(ref _valueChanged, value);
        }

        /// <summary>Raised when Enter is pressed in a single-line text field.</summary>
        public event Action<RuntimeUiElement> Submitted
        {
            add => Subscribe(ref _submitted, value);
            remove => Unsubscribe(ref _submitted, value);
        }

        /// <summary>Raised when the pointer enters the element or one of its descendants.</summary>
        public event Action<RuntimeUiElement> PointerEntered
        {
            add => Subscribe(ref _pointerEntered, value);
            remove => Unsubscribe(ref _pointerEntered, value);
        }

        /// <summary>Raised when the pointer leaves the element.</summary>
        public event Action<RuntimeUiElement> PointerExited
        {
            add => Subscribe(ref _pointerExited, value);
            remove => Unsubscribe(ref _pointerExited, value);
        }

        /// <summary>Raised when keyboard focus moves into the element (or into the field it is).</summary>
        public event Action<RuntimeUiElement> FocusGained
        {
            add => Subscribe(ref _focusGained, value);
            remove => Unsubscribe(ref _focusGained, value);
        }

        /// <summary>Raised when keyboard focus leaves the element.</summary>
        public event Action<RuntimeUiElement> FocusLost
        {
            add => Subscribe(ref _focusLost, value);
            remove => Unsubscribe(ref _focusLost, value);
        }

        /// <summary>
        /// Raised when a scroll view's offset changes by anything but <see cref="SetScrollOffset"/>: the mouse
        /// wheel, a touch drag, a scroll bar, the keyboard, or the view clamping the offset after its content
        /// shrank. Any other kind never raises it.
        /// </summary>
        public event Action<RuntimeUiElement> ScrollChanged
        {
            add => Subscribe(ref _scrollChanged, value);
            remove => Unsubscribe(ref _scrollChanged, value);
        }

        /// <summary>The screen the element belongs to.</summary>
        public RuntimeUiScreen Screen { get; }

        /// <summary>True while the handle points at a live element.</summary>
        public bool IsAlive => Record != null && !Screen.IsDestroyed;

        /// <summary>The element kind.</summary>
        public RuntimeUiElementKind Kind { get; private set; }

        /// <summary>The element name (or the name the handle rebinds by).</summary>
        public string Name => Record != null ? Record.Element.name : BindName;

        /// <summary>The UI Toolkit type name matched by type selectors.</summary>
        public string TypeName => Record?.TypeName ?? UxmlElementCatalog.TypeNameOf(Kind);

        /// <summary>
        /// The underlying visual element, for adapters inside this codebase. Inline styles written on it
        /// directly are overwritten by the next restyle; use <see cref="SetStyle(string, string)"/>.
        /// </summary>
        public VisualElement VisualElement => Record?.Element;

        /// <summary>True when the element is displayed (its resolved <c>display</c> is not <c>none</c>).</summary>
        public bool Visible
        {
            get => Record != null && !Record.DisplayNone;
            set => SetStyle(UssPropertyId.Display, UssValue.FromKeyword(value ? UssKeywords.Flex : UssKeywords.None));
        }

        /// <summary>True when the element and its ancestors are enabled.</summary>
        public bool Enabled => Record != null && Record.Element.enabledInHierarchy;

        /// <summary>The text: label or button text, toggle text, text-field value, progress title, slider label.</summary>
        public string Text
        {
            get
            {
                VisualElement element = Record?.Element;
                switch (element)
                {
                    case TextField field:
                        return field.value;
                    case TextElement text:
                        return text.text;
                    case Toggle toggle:
                        return toggle.text;
                    case ProgressBar bar:
                        return bar.title;
                    case Slider slider:
                        return slider.label;
                    default:
                        return null;
                }
            }
        }

        /// <summary>Stacking order among siblings: higher draws on top; equal values keep document order.</summary>
        public int ZOrder => _zOrder;

        /// <summary>
        /// How far a scroll view's content is scrolled, in pixels (x moves it left, y up); zero for any other kind.
        /// The view's range is its content's extent minus its viewport, so a layer that places elements itself gives
        /// the content its size.
        /// </summary>
        public Vector2 ScrollOffset => Record?.Element is ScrollView view ? view.scrollOffset : Vector2.zero;

        /// <summary>The parent element's handle, or null at the screen root.</summary>
        public RuntimeUiElement Parent
        {
            get
            {
                RuntimeUiNodeRecord parent = Record?.Parent;
                while (parent != null && !parent.IsAuthored && parent != Screen.RootRecord)
                {
                    parent = parent.Parent;
                }

                return parent == null ? null : Screen.GetHandle(parent);
            }
        }

        internal RuntimeUiNodeRecord Record { get; private set; }

        internal string BindName { get; }

        internal int BindOccurrence { get; }

        internal List<UssDeclaration> StyleOverrides { get; } = new();

        internal IReadOnlyList<string> ListItems => _listItems;

        /// <summary>Sets the text (see <see cref="Text"/> for what each kind maps it to).</summary>
        /// <returns>False when the element is detached or its kind has no text.</returns>
        public bool SetText(string text)
        {
            if (!ApplyText(text ?? string.Empty))
            {
                return false;
            }

            _textOverride = text ?? string.Empty;
            _hasTextOverride = true;
            return true;
        }

        /// <summary>Reads the value as text: <c>true</c>/<c>false</c>, a number, the field text or the selected index.</summary>
        public bool TryGetValue(out string value)
        {
            value = null;
            switch (Record?.Element)
            {
                case Toggle toggle:
                    value = toggle.value ? "true" : "false";
                    return true;
                case Slider slider:
                    value = slider.value.ToString("R", CultureInfo.InvariantCulture);
                    return true;
                case ProgressBar bar:
                    value = bar.value.ToString("R", CultureInfo.InvariantCulture);
                    return true;
                case TextField field:
                    value = field.value;
                    return true;
                case ListView list:
                    value = list.selectedIndex.ToString(CultureInfo.InvariantCulture);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Sets the value from text (parsed per kind). Does not raise <see cref="ValueChanged"/>.</summary>
        public bool SetValue(string value)
        {
            switch (Record?.Element)
            {
                case Toggle _:
                    return UxmlValueParsing.TryParseBool(value, out bool flag) && SetBool(flag);
                case Slider _:
                case ProgressBar _:
                case ListView _:
                    return UxmlValueParsing.TryParseFloat(value, out float number) && SetNumber(number);
                case TextField field:
                    field.SetValueWithoutNotify(value ?? string.Empty);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Reads a numeric value (slider, progress bar, list selection).</summary>
        public bool TryGetNumber(out float value)
        {
            switch (Record?.Element)
            {
                case Slider slider:
                    value = slider.value;
                    return true;
                case ProgressBar bar:
                    value = bar.value;
                    return true;
                case ListView list:
                    value = list.selectedIndex;
                    return true;
                default:
                    value = 0f;
                    return false;
            }
        }

        /// <summary>Sets a numeric value without raising <see cref="ValueChanged"/>.</summary>
        public bool SetNumber(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return false;
            }

            switch (Record?.Element)
            {
                case Slider slider:
                    slider.SetValueWithoutNotify(value);
                    return true;
                case ProgressBar bar:
                    bar.SetValueWithoutNotify(value);
                    return true;
                case ListView list:
                    int index = Mathf.RoundToInt(value);
                    list.SetSelectionWithoutNotify(index < 0 ? Array.Empty<int>() : new[] { index });
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Reads a toggle.</summary>
        public bool TryGetBool(out bool value)
        {
            if (Record?.Element is Toggle toggle)
            {
                value = toggle.value;
                return true;
            }

            value = false;
            return false;
        }

        /// <summary>Sets a toggle without raising <see cref="ValueChanged"/> (its <c>:checked</c> style updates).</summary>
        public bool SetBool(bool value)
        {
            if (Record?.Element is not Toggle toggle)
            {
                return false;
            }

            toggle.SetValueWithoutNotify(value);
            Screen.OnCheckedChanged(Record, value);
            return true;
        }

        /// <summary>Enables or disables the element and its descendants (their <c>:disabled</c> styles update).</summary>
        public bool SetEnabled(bool enabled)
        {
            if (!IsAlive)
            {
                return false;
            }

            _enabledOverride = enabled;
            Record.Element.SetEnabled(enabled);
            Screen.RestyleSubtree(Record);
            return true;
        }

        /// <summary>Adds a class and restyles the element's subtree.</summary>
        public bool AddClass(string className)
        {
            return ChangeClass(className, true);
        }

        /// <summary>Removes a class and restyles the element's subtree.</summary>
        public bool RemoveClass(string className)
        {
            return ChangeClass(className, false);
        }

        /// <summary>Adds the class when absent, removes it when present.</summary>
        public bool ToggleClass(string className)
        {
            return IsAlive && ChangeClass(className, !Record.Element.ClassListContains(className));
        }

        /// <summary>True when the element carries the class.</summary>
        public bool HasClass(string className)
        {
            return IsAlive && !string.IsNullOrEmpty(className) && Record.Element.ClassListContains(className);
        }

        /// <summary>Copies the element's classes into <paramref name="output"/> (cleared first).</summary>
        public int GetClasses(List<string> output)
        {
            output.Clear();
            if (!IsAlive)
            {
                return 0;
            }

            foreach (string className in Record.Element.GetClasses())
            {
                output.Add(className);
            }

            return output.Count;
        }

        /// <summary>
        /// Sets one style property from USS text (a longhand, a shorthand or a <c>--custom</c> property;
        /// <c>var()</c> allowed). It wins over every rule and the <c>style</c> attribute, and survives USS and
        /// UXML replaces. Problems are reported in the screen's diagnostics.
        /// </summary>
        /// <returns>False when the element is detached, the property is unsupported or the value invalid.</returns>
        public bool SetStyle(string property, string value)
        {
            if (!IsAlive)
            {
                return false;
            }

            RuntimeUiDiagnosticList reports = new(8);
            UssDeclaration declaration = UssDeclaration.Create(property, value, 0, 0, RuntimeUiSource.Api, reports);
            Screen.AddDiagnostics(reports);
            if (declaration == null)
            {
                return false;
            }

            ReplaceOverride(declaration);
            Screen.RestyleAfterOverride(Record, declaration.IsCustom);
            return true;
        }

        /// <summary>Sets one longhand to a typed value without parsing text (the zero-parse path for C# and Roblox layers).</summary>
        public bool SetStyle(UssPropertyId property, UssValue value)
        {
            if (!IsAlive || property < 0 || property >= UssPropertyId.Count || !value.HasValue)
            {
                return false;
            }

            ReplaceOverride(UssDeclaration.FromValue(property, value));
            Screen.RestyleAfterOverride(Record, false);
            return true;
        }

        /// <summary>Removes a style set through <see cref="SetStyle(string, string)"/>; rules apply again.</summary>
        public bool ClearStyle(string property)
        {
            if (!IsAlive || string.IsNullOrWhiteSpace(property))
            {
                return false;
            }

            string name = property.Trim();
            string lower = UssPropertyCatalog.IsCustomProperty(name) ? name : name.ToLowerInvariant();
            bool removed = false;
            bool custom = UssPropertyCatalog.IsCustomProperty(lower);
            for (int i = StyleOverrides.Count - 1; i >= 0; i--)
            {
                if (StyleOverrides[i].Property == lower)
                {
                    StyleOverrides.RemoveAt(i);
                    removed = true;
                }
            }

            if (removed)
            {
                Screen.RestyleAfterOverride(Record, custom);
            }

            return removed;
        }

        /// <summary>Removes every style set through the API.</summary>
        public void ClearAllStyles()
        {
            if (StyleOverrides.Count == 0)
            {
                return;
            }

            StyleOverrides.Clear();
            if (IsAlive)
            {
                Screen.RestyleAfterOverride(Record, true);
            }
        }

        /// <summary>The value the cascade resolved for a longhand on this element right now.</summary>
        public bool TryGetResolvedStyle(UssPropertyId property, out UssValue value)
        {
            if (!IsAlive)
            {
                value = default;
                return false;
            }

            return Screen.TryResolveStyle(Record, property, out value);
        }

        /// <summary>Sets a UXML attribute (same names and values as in markup, such as <c>low-value</c>).</summary>
        public bool SetAttribute(string name, string value)
        {
            if (!IsAlive || string.IsNullOrWhiteSpace(name) || name == "name")
            {
                return false;
            }

            if (!Screen.Factory.ApplyAttribute(Record.Element, Kind, name.Trim(), value, out string error))
            {
                Screen.AddDiagnostic(RuntimeUiDiagnosticCodes.UxmlInvalidAttributeValue, RuntimeUiSource.Api,
                    "SetAttribute('" + name + "') on " + Describe() + ": " + error);
                return false;
            }

            for (int i = _attributeOverrides.Count - 1; i >= 0; i--)
            {
                if (_attributeOverrides[i].Key == name.Trim())
                {
                    _attributeOverrides.RemoveAt(i);
                }
            }

            _attributeOverrides.Add(new KeyValuePair<string, string>(name.Trim(), value));
            if (name.Trim() == "class" || name.Trim() == "enabled")
            {
                Screen.RefreshTrackingAndRestyle(Record);
            }

            return true;
        }

        /// <summary>Sets the items of a list view (each item shows as text).</summary>
        public bool SetItems(IReadOnlyList<string> items)
        {
            if (!IsAlive || Kind != RuntimeUiElementKind.ListView)
            {
                return false;
            }

            _listItems ??= new List<string>();
            _listItems.Clear();
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    _listItems.Add(items[i] ?? string.Empty);
                }
            }

            ApplyListItems();
            return true;
        }

        /// <summary>Shows a texture in an image (or as a background for other kinds).</summary>
        public bool SetImage(Texture2D texture)
        {
            return texture != null && ApplyImage(Background.FromTexture2D(texture));
        }

        /// <summary>Shows a sprite in an image (or as a background for other kinds).</summary>
        public bool SetImage(Sprite sprite)
        {
            return sprite != null && ApplyImage(Background.FromSprite(sprite));
        }

        /// <summary>Sets the stacking order among siblings and re-sorts them (stable for equal values).</summary>
        public bool SetZOrder(int zOrder)
        {
            if (!IsAlive)
            {
                return false;
            }

            _zOrder = zOrder;
            _hasZOrder = true;
            Screen.SortSiblings(Record);
            return true;
        }

        /// <summary>
        /// Scrolls a scroll view to <paramref name="offset"/> (the view clamps it to its range) without raising
        /// <see cref="ScrollChanged"/>.
        /// </summary>
        /// <returns>False when the element is detached, is not a scroll view, or the offset is not finite.</returns>
        public bool SetScrollOffset(Vector2 offset)
        {
            if (!IsAlive || !(Record.Element is ScrollView view) || !IsFinite(offset.x) || !IsFinite(offset.y))
            {
                return false;
            }

            // WHY the flag and the read-back: the scrollers raise their change for this write too, synchronously
            // or after the event being dispatched, and only a value different from the known one is the player's.
            _applyingScroll = true;
            try
            {
                view.scrollOffset = offset;
            }
            finally
            {
                _applyingScroll = false;
            }

            _knownScroll = view.scrollOffset;
            return true;
        }

        /// <summary>Moves the element after all its siblings (drawn on top).</summary>
        public bool BringToFront()
        {
            if (!IsAlive || Record == Screen.RootRecord)
            {
                return false;
            }

            Record.Element.BringToFront();
            return true;
        }

        /// <summary>Moves the element before all its siblings (drawn below).</summary>
        public bool SendToBack()
        {
            if (!IsAlive || Record == Screen.RootRecord)
            {
                return false;
            }

            Record.Element.SendToBack();
            return true;
        }

        /// <summary>Gives the element keyboard focus.</summary>
        public bool Focus()
        {
            if (!IsAlive)
            {
                return false;
            }

            Record.Element.Focus();
            return true;
        }

        /// <summary>
        /// Gives a text field keyboard focus for typing: the caret goes after the text, focus is asked again on the
        /// next layout pass when the field could not take it yet (it was hidden a moment ago), and while
        /// <paramref name="heldKeyCodes"/> is not empty the keystroke being pressed does not type into it
        /// (<see cref="OpeningKeystrokeFilter"/>). Other elements are focused as <see cref="Focus"/> does.
        /// </summary>
        public bool FocusForTyping(IReadOnlyList<int> heldKeyCodes)
        {
            if (!IsAlive)
            {
                return false;
            }

            VisualElement element = Record.Element;
            if (!(element is TextField))
            {
                return Focus();
            }

            if (heldKeyCodes != null && heldKeyCodes.Count > 0)
            {
                _openingKeystroke.Arm(heldKeyCodes, Time.frameCount);
                if (!ReferenceEquals(_typingKeyDownOn, element))
                {
                    _typingKeyDownOn?.UnregisterCallback(_onTypingKeyDown, TrickleDown.TrickleDown);
                    element.RegisterCallback(_onTypingKeyDown, TrickleDown.TrickleDown);
                    _typingKeyDownOn = element;
                }
            }
            else
            {
                _openingKeystroke.Disarm();
            }

            FocusAtEnd(element);
            if (element.panel != null && element.focusController?.focusedElement != element)
            {
                // WHY: a field that was display:none a moment ago cannot take focus until the next layout pass.
                element.schedule.Execute(() =>
                {
                    if (IsAlive && ReferenceEquals(Record.Element, element))
                    {
                        FocusAtEnd(element);
                    }
                });
            }

            return true;
        }

        /// <summary>
        /// Measures <paramref name="text"/> as this label or button draws it at <paramref name="fontSize"/> pixels;
        /// <paramref name="maxWidth"/> above 0 wraps it at that width. The element's resolved style supplies the font,
        /// and the result is scaled from its resolved size to <paramref name="fontSize"/>, so a size pushed this frame is
        /// already honoured. False for an element that draws no text or is not on a panel yet.
        /// </summary>
        public bool TryMeasureText(string text, float fontSize, float maxWidth, out float width, out float height)
        {
            width = 0f;
            height = 0f;
            if (!IsAlive || !(Record.Element is TextElement element) || element.panel == null)
            {
                return false;
            }

            float resolved = element.resolvedStyle.fontSize;
            float scale = resolved > 0f && fontSize > 0f ? fontSize / resolved : 1f;
            bool wrap = maxWidth > 0f;
            Vector2 measured = element.MeasureTextSize(text ?? "", wrap ? maxWidth / scale : 0f,
                wrap ? VisualElement.MeasureMode.AtMost : VisualElement.MeasureMode.Undefined, 0f,
                VisualElement.MeasureMode.Undefined);
            if (!IsFinite(measured.x) || !IsFinite(measured.y))
            {
                return false;
            }

            width = measured.x * scale;
            height = measured.y * scale;
            return true;
        }

        private static void FocusAtEnd(VisualElement element)
        {
            element.Focus();
            if (element is TextField field)
            {
                int end = (field.value ?? "").Length;
                field.SelectRange(end, end);
            }
        }

        /// <summary>Takes keyboard focus away from the element.</summary>
        public bool Blur()
        {
            if (!IsAlive)
            {
                return false;
            }

            Record.Element.Blur();
            return true;
        }

        /// <summary>The first authored descendant matching a selector, or null.</summary>
        public RuntimeUiElement Query(string selector)
        {
            return IsAlive ? Screen.QueryFrom(Record, selector) : null;
        }

        /// <summary>Creates a child element of a kind (see <see cref="RuntimeUiScreen.CreateElement"/>).</summary>
        public RuntimeUiElement CreateChild(RuntimeUiElementKind kind, string name = null)
        {
            return IsAlive ? Screen.CreateElement(kind, this, name) : null;
        }

        /// <summary>Removes the element (and its subtree) from the screen.</summary>
        public bool Remove()
        {
            return IsAlive && Screen.RemoveElement(this);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Describe();
        }

        internal string Describe()
        {
            string name = Name;
            return TypeName + (string.IsNullOrEmpty(name) ? string.Empty : "#" + name) + (IsAlive ? string.Empty : " (detached)");
        }

        internal int GetZOrderOrDefault()
        {
            return _hasZOrder ? _zOrder : 0;
        }

        internal bool HasZOrder => _hasZOrder;

        internal void Retarget(RuntimeUiNodeRecord record)
        {
            UnregisterEvents();
            Record = record;
            Kind = record.Kind;
            record.Handle = this;
            ReplayOverrides();
            RegisterEvents();
        }

        internal void Detach(bool clearSubscribers)
        {
            UnregisterEvents();
            if (Record != null && Record.Handle == this)
            {
                Record.Handle = null;
            }

            Record = null;
            _focused = false;
            if (clearSubscribers)
            {
                _clicked = null;
                _valueChanged = null;
                _submitted = null;
                _pointerEntered = null;
                _pointerExited = null;
                _focusGained = null;
                _focusLost = null;
                _scrollChanged = null;
            }
        }

        internal bool HasRegisteredEvents => _registered != EventGroups.None;

        internal int SubscriberCount
        {
            get
            {
                return Count(_clicked) + Count(_valueChanged) + Count(_submitted) + Count(_pointerEntered) +
                       Count(_pointerExited) + Count(_focusGained) + Count(_focusLost) + Count(_scrollChanged);
            }
        }

        internal void ReplayClassesAndEnabled()
        {
            if (Record == null)
            {
                return;
            }

            for (int i = 0; i < _classLog.Count; i++)
            {
                Record.Element.EnableInClassList(_classLog[i].Key, _classLog[i].Value);
            }

            if (_enabledOverride.HasValue)
            {
                Record.Element.SetEnabled(_enabledOverride.Value);
            }
        }

        private void ReplayOverrides()
        {
            for (int i = 0; i < _attributeOverrides.Count; i++)
            {
                Screen.Factory.ApplyAttribute(Record.Element, Kind, _attributeOverrides[i].Key,
                    _attributeOverrides[i].Value, out _);
            }

            if (_hasTextOverride)
            {
                ApplyText(_textOverride);
            }

            ReplayClassesAndEnabled();
            if (_listItems != null)
            {
                ApplyListItems();
            }

            if (_hasImage)
            {
                ApplyImage(_image);
            }
        }

        private bool ApplyText(string text)
        {
            switch (Record?.Element)
            {
                case TextField field:
                    field.SetValueWithoutNotify(text);
                    return true;
                case TextElement element:
                    element.text = text;
                    return true;
                case Toggle toggle:
                    toggle.text = text;
                    return true;
                case ProgressBar bar:
                    bar.title = text;
                    return true;
                case Slider slider:
                    slider.label = text;
                    return true;
                default:
                    return false;
            }
        }

        private bool ApplyImage(Background background)
        {
            if (!IsAlive)
            {
                return false;
            }

            if (Record.Element is Image image)
            {
                if (background.sprite != null)
                {
                    image.sprite = background.sprite;
                }
                else
                {
                    image.image = background.texture;
                }
            }
            else
            {
                Record.Element.style.backgroundImage = new StyleBackground(background);
            }

            _image = background;
            _hasImage = true;
            return true;
        }

        private void ApplyListItems()
        {
            if (Record?.Element is ListView list)
            {
                Screen.ConfigureListView(Record);
                list.itemsSource = _listItems;
                list.RefreshItems();
            }
        }

        private bool ChangeClass(string className, bool add)
        {
            if (!IsAlive || string.IsNullOrWhiteSpace(className))
            {
                return false;
            }

            string trimmed = className.Trim();
            for (int i = _classLog.Count - 1; i >= 0; i--)
            {
                if (_classLog[i].Key == trimmed)
                {
                    _classLog.RemoveAt(i);
                }
            }

            _classLog.Add(new KeyValuePair<string, bool>(trimmed, add));
            bool had = Record.Element.ClassListContains(trimmed);
            if (had == add)
            {
                return true;
            }

            Record.Element.EnableInClassList(trimmed, add);
            Screen.RefreshTrackingAndRestyle(Record);
            return true;
        }

        private void ReplaceOverride(UssDeclaration declaration)
        {
            for (int i = StyleOverrides.Count - 1; i >= 0; i--)
            {
                if (StyleOverrides[i].Property == declaration.Property)
                {
                    StyleOverrides.RemoveAt(i);
                }
            }

            StyleOverrides.Add(declaration);
        }

        private void Subscribe(ref Action<RuntimeUiElement> field, Action<RuntimeUiElement> handler)
        {
            if (handler == null)
            {
                return;
            }

            field += handler;
            RegisterEvents();
        }

        private void Unsubscribe(ref Action<RuntimeUiElement> field, Action<RuntimeUiElement> handler)
        {
            if (handler == null)
            {
                return;
            }

            field -= handler;
            RegisterEvents();
        }

        private EventGroups WantedGroups()
        {
            EventGroups wanted = EventGroups.None;
            if (_clicked != null)
            {
                wanted |= EventGroups.Click;
            }

            if (_valueChanged != null)
            {
                wanted |= EventGroups.Value;
            }

            if (_submitted != null)
            {
                wanted |= EventGroups.Submit;
            }

            if (_pointerEntered != null || _pointerExited != null)
            {
                wanted |= EventGroups.Pointer;
            }

            if (_focusGained != null || _focusLost != null)
            {
                wanted |= EventGroups.Focus;
            }

            if (_scrollChanged != null)
            {
                wanted |= EventGroups.Scroll;
            }

            return wanted;
        }

        private void RegisterEvents()
        {
            VisualElement element = IsAlive ? Record.Element : null;
            if (element != _registeredOn)
            {
                UnregisterEvents();
            }

            if (element == null)
            {
                return;
            }

            EventGroups wanted = WantedGroups();
            EventGroups add = wanted & ~_registered;
            EventGroups remove = _registered & ~wanted;
            if (remove != EventGroups.None)
            {
                Unregister(element, remove);
            }

            if (add != EventGroups.None)
            {
                Register(element, add);
            }

            _registered = wanted;
            _registeredOn = wanted == EventGroups.None ? null : element;
        }

        private void UnregisterEvents()
        {
            if (_registeredOn != null && _registered != EventGroups.None)
            {
                Unregister(_registeredOn, _registered);
            }

            _registered = EventGroups.None;
            _registeredOn = null;
        }

        private void Register(VisualElement element, EventGroups groups)
        {
            if ((groups & EventGroups.Click) != 0)
            {
                if (element is Button button)
                {
                    button.clicked += _onButtonClicked;
                }
                else
                {
                    element.RegisterCallback(_onClick);
                }
            }

            if ((groups & EventGroups.Value) != 0)
            {
                switch (element)
                {
                    case Toggle _:
                        element.RegisterCallback(_onBoolChanged);
                        break;
                    case Slider _:
                    case ProgressBar _:
                        element.RegisterCallback(_onFloatChanged);
                        break;
                    case TextField _:
                        element.RegisterCallback(_onStringChanged);
                        break;
                    case ListView list:
                        list.selectedIndicesChanged += _onSelectionChanged;
                        break;
                }
            }

            if ((groups & EventGroups.Submit) != 0 && element is TextField)
            {
                element.RegisterCallback(_onKeyDown, TrickleDown.TrickleDown);
            }

            if ((groups & EventGroups.Pointer) != 0)
            {
                element.RegisterCallback(_onPointerEnter);
                element.RegisterCallback(_onPointerLeave);
            }

            if ((groups & EventGroups.Focus) != 0)
            {
                element.RegisterCallback(_onFocusIn);
                element.RegisterCallback(_onFocusOut);
            }

            if ((groups & EventGroups.Scroll) != 0 && element is ScrollView view)
            {
                view.horizontalScroller.valueChanged += _onScrollerValueChanged;
                view.verticalScroller.valueChanged += _onScrollerValueChanged;
                _knownScroll = view.scrollOffset;
            }
        }

        private void Unregister(VisualElement element, EventGroups groups)
        {
            if ((groups & EventGroups.Click) != 0)
            {
                if (element is Button button)
                {
                    button.clicked -= _onButtonClicked;
                }
                else
                {
                    element.UnregisterCallback(_onClick);
                }
            }

            if ((groups & EventGroups.Value) != 0)
            {
                element.UnregisterCallback(_onBoolChanged);
                element.UnregisterCallback(_onFloatChanged);
                element.UnregisterCallback(_onStringChanged);
                if (element is ListView list)
                {
                    list.selectedIndicesChanged -= _onSelectionChanged;
                }
            }

            if ((groups & EventGroups.Submit) != 0)
            {
                element.UnregisterCallback(_onKeyDown, TrickleDown.TrickleDown);
            }

            if ((groups & EventGroups.Pointer) != 0)
            {
                element.UnregisterCallback(_onPointerEnter);
                element.UnregisterCallback(_onPointerLeave);
            }

            if ((groups & EventGroups.Focus) != 0)
            {
                element.UnregisterCallback(_onFocusIn);
                element.UnregisterCallback(_onFocusOut);
            }

            if ((groups & EventGroups.Scroll) != 0 && element is ScrollView view)
            {
                view.horizontalScroller.valueChanged -= _onScrollerValueChanged;
                view.verticalScroller.valueChanged -= _onScrollerValueChanged;
            }
        }

        private void OnClick(ClickEvent evt)
        {
            Screen.Raise(_clicked, this, "Clicked");
        }

        private void OnButtonClicked()
        {
            Screen.Raise(_clicked, this, "Clicked");
        }

        private void OnBoolChanged(ChangeEvent<bool> evt)
        {
            if (evt.target == Record?.Element)
            {
                Screen.Raise(_valueChanged, this, "ValueChanged");
            }
        }

        private void OnFloatChanged(ChangeEvent<float> evt)
        {
            if (evt.target == Record?.Element)
            {
                Screen.Raise(_valueChanged, this, "ValueChanged");
            }
        }

        private void OnStringChanged(ChangeEvent<string> evt)
        {
            if (evt.target == Record?.Element)
            {
                Screen.Raise(_valueChanged, this, "ValueChanged");
            }
        }

        private void OnSelectionChanged(IEnumerable<int> indices)
        {
            Screen.Raise(_valueChanged, this, "ValueChanged");
        }

        private void OnTypingKeyDown(KeyDownEvent evt)
        {
            // WHY skipped then: the submit listener on the same field already filters the keystroke (so the opening
            // Enter never submits), and each event is checked once.
            if ((_registered & EventGroups.Submit) != 0 && ReferenceEquals(_registeredOn, _typingKeyDownOn))
            {
                return;
            }

            SwallowOpeningKeystroke(evt);
        }

        private bool SwallowOpeningKeystroke(KeyDownEvent evt)
        {
            if (!_openingKeystroke.IsArmed
                || !_openingKeystroke.ShouldSwallow((int)evt.keyCode, evt.character, Time.frameCount))
            {
                return false;
            }

            evt.StopImmediatePropagation();
            VisualElement target = evt.target as VisualElement ?? Record?.Element;
            target?.focusController?.IgnoreEvent(evt);
            return true;
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (SwallowOpeningKeystroke(evt))
            {
                return;
            }

            if (Record?.Element is TextField field && !field.multiline
                                                    && (evt.keyCode == KeyCode.Return
                                                        || evt.keyCode == KeyCode.KeypadEnter))
            {
                Screen.Raise(_submitted, this, "Submitted");
            }
        }

        private void OnPointerEnter(PointerEnterEvent evt)
        {
            Screen.Raise(_pointerEntered, this, "PointerEntered");
        }

        private void OnPointerLeave(PointerLeaveEvent evt)
        {
            Screen.Raise(_pointerExited, this, "PointerExited");
        }

        private void OnFocusIn(FocusInEvent evt)
        {
            if (_focused || !RuntimeUiScreen.IsFocusWithin(Record, evt.target as VisualElement))
            {
                return;
            }

            _focused = true;
            Screen.Raise(_focusGained, this, "FocusGained");
        }

        private void OnFocusOut(FocusOutEvent evt)
        {
            if (!_focused || RuntimeUiScreen.IsFocusWithin(Record, evt.relatedTarget as VisualElement))
            {
                return;
            }

            _focused = false;
            Screen.Raise(_focusLost, this, "FocusLost");
        }

        private void OnScrollerValueChanged(float value)
        {
            if (_applyingScroll || !(Record?.Element is ScrollView view))
            {
                return;
            }

            Vector2 offset = view.scrollOffset;
            if (Mathf.Approximately(offset.x, _knownScroll.x) && Mathf.Approximately(offset.y, _knownScroll.y))
            {
                return;
            }

            _knownScroll = offset;
            Screen.Raise(_scrollChanged, this, "ScrollChanged");
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static int Count(Action<RuntimeUiElement> handlers)
        {
            return handlers == null ? 0 : handlers.GetInvocationList().Length;
        }
    }
}
