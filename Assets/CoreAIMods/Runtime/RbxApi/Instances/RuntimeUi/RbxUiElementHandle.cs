using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// One element of a mod-owned screen as scripts see it. Reads of a detached element return null
    /// (or false); every write raises <c>UI_ELEMENT_DETACHED</c>, is charged to the owning mod's
    /// per-frame write budget, and raises <c>UI_UNSUPPORTED_OPERATION</c> when the kind has no such
    /// member. The three events are ordinary deferred <see cref="RbxScriptSignal"/>s carrying plain
    /// values: <see cref="Clicked"/> nothing, <see cref="ValueChanged"/> the new value, and
    /// <see cref="Submitted"/> the field text.
    /// </summary>
    public sealed class RbxUiElementHandle
    {
        private readonly Action _onClicked;
        private readonly Action _onValueChanged;
        private readonly Action _onSubmitted;
        private RbxScriptSignal _clicked;
        private RbxScriptSignal _valueChanged;
        private RbxScriptSignal _submitted;
        private bool _released;

        internal RbxUiElementHandle(RbxUiScreenHandle screen, IRbxUiElement port)
        {
            Screen = screen;
            Port = port;
            _onClicked = OnClicked;
            _onValueChanged = OnValueChanged;
            _onSubmitted = OnSubmitted;
        }

        /// <summary>The screen the element belongs to.</summary>
        public RbxUiScreenHandle Screen { get; }

        /// <summary>The backend element.</summary>
        public IRbxUiElement Port { get; }

        /// <summary>True while the element is part of a live screen.</summary>
        public bool IsAlive => !_released && !Screen.IsDestroyed && Port.IsAlive;

        /// <summary>The element name, or null.</summary>
        public string Name => Port.Name;

        /// <summary>The UXML type name.</summary>
        public string Kind => Port.Kind;

        /// <summary>The parent element, or null at the screen root or when detached.</summary>
        public RbxUiElementHandle Parent => IsAlive ? Screen.Wrap(Port.Parent) : null;

        /// <summary>The text, or null when detached or the kind has none.</summary>
        public string Text => IsAlive ? Port.Text : null;

        /// <summary>True when displayed; false when detached.</summary>
        public bool Visible => IsAlive && Port.Visible;

        /// <summary>True when enabled in the hierarchy; false when detached.</summary>
        public bool Enabled => IsAlive && Port.Enabled;

        /// <summary>Stacking order among siblings.</summary>
        public int ZOrder => IsAlive ? Port.ZOrder : 0;

        /// <summary>Opaque per-binding cache slot (the Lua proxy of this element).</summary>
        public object BindingProxy { get; set; }

        /// <summary>The owner of <see cref="BindingProxy"/>.</summary>
        public object BindingProxyOwner { get; set; }

        /// <summary>Fired with no arguments when the element is clicked.</summary>
        public RbxScriptSignal Clicked => _clicked ??= CreateSignal("UiElement.Clicked", true, false, false);

        /// <summary>Fired with the new value (boolean, number or text) when the player changes it.</summary>
        public RbxScriptSignal ValueChanged =>
            _valueChanged ??= CreateSignal("UiElement.ValueChanged", false, true, false);

        /// <summary>Fired with the field text when Enter is pressed in a single-line text field.</summary>
        public RbxScriptSignal Submitted => _submitted ??= CreateSignal("UiElement.Submitted", false, false, true);

        /// <summary>Raises <c>UI_ELEMENT_DETACHED</c> (or <c>UI_SCREEN_DESTROYED</c>) once the element is gone.</summary>
        public void RequireAlive(string member)
        {
            Screen.RequireAlive(member);
            if (!IsAlive)
            {
                throw RbxUiErrors.Create(RbxUiErrors.ElementDetached,
                    member + " on a detached element (" + Describe() + ")",
                    "name the elements you keep (a UXML replace keeps named ones) and look the element up "
                    + "again with Query or GetElement; check IsAlive first");
            }
        }

        /// <summary>The value, or false when detached or the kind has none.</summary>
        public bool TryGetValue(out RbxUiValue value)
        {
            if (!IsAlive)
            {
                value = default;
                return false;
            }

            return Port.TryGetValue(out value);
        }

        /// <summary>Sets the text.</summary>
        public void SetText(string text)
        {
            const string member = "UiElement.Text";
            BeginWrite(member);
            Screen.World.CheckText(text, member, "the text");
            if (!Port.SetText(text ?? string.Empty))
            {
                throw Unsupported(member, "has no text", "use a Label, Button, Toggle, TextField, ProgressBar or Slider");
            }
        }

        /// <summary>Sets the value without firing <see cref="ValueChanged"/>.</summary>
        public void SetValue(RbxUiValue value)
        {
            const string member = "UiElement.Value";
            BeginWrite(member);
            if (value.Kind == RbxUiValueKind.Text)
            {
                Screen.World.CheckText(value.Text, member, "the value");
            }

            if (!Port.SetValue(value))
            {
                throw Unsupported(member, "takes no " + DescribeKind(value.Kind) + " value",
                    "a Toggle takes a boolean, a Slider, ProgressBar or ListView a number, a TextField text; "
                    + "set Text for labels and buttons");
            }
        }

        /// <summary>Shows or hides the element.</summary>
        public void SetVisible(bool visible)
        {
            BeginWrite("UiElement.Visible");
            Port.SetVisible(visible);
        }

        /// <summary>Enables or disables the element and its subtree.</summary>
        public void SetEnabled(bool enabled)
        {
            BeginWrite("UiElement.Enabled");
            Port.SetEnabled(enabled);
        }

        /// <summary>Sets the stacking order among siblings.</summary>
        public void SetZOrder(int zOrder)
        {
            BeginWrite("UiElement.ZOrder");
            Port.SetZOrder(zOrder);
        }

        /// <summary>Adds a class.</summary>
        public void AddClass(string className)
        {
            const string member = "UiElement:AddClass";
            BeginWrite(member);
            Port.AddClass(RequireClassName(className, member));
        }

        /// <summary>Removes a class.</summary>
        public void RemoveClass(string className)
        {
            const string member = "UiElement:RemoveClass";
            BeginWrite(member);
            Port.RemoveClass(RequireClassName(className, member));
        }

        /// <summary>Adds the class when absent, removes it when present; returns whether it is present now.</summary>
        public bool ToggleClass(string className)
        {
            const string member = "UiElement:ToggleClass";
            BeginWrite(member);
            string name = RequireClassName(className, member);
            Port.ToggleClass(name);
            return Port.HasClass(name);
        }

        /// <summary>True when the element carries the class; false when detached.</summary>
        public bool HasClass(string className)
        {
            return IsAlive && !string.IsNullOrWhiteSpace(className) && Port.HasClass(className.Trim());
        }

        /// <summary>Copies the classes into <paramref name="output"/> (cleared first; empty when detached).</summary>
        public void GetClasses(List<string> output)
        {
            output.Clear();
            if (IsAlive)
            {
                Port.GetClasses(output);
            }
        }

        /// <summary>
        /// Sets one style property from USS text. Returns false when the property is unsupported or the
        /// value invalid; the report is in the screen's diagnostics, never raised.
        /// </summary>
        public bool SetStyle(string property, string value)
        {
            const string member = "UiElement:SetStyle";
            BeginWrite(member);
            if (string.IsNullOrWhiteSpace(property))
            {
                throw RbxUiErrors.Create(RbxUiErrors.InvalidArgument, member + " expects a property name",
                    "pass a USS property such as \"background-color\" or a token such as \"--cai-color-accent\"");
            }

            Screen.World.CheckText(value, member, "the value");
            return Port.SetStyle(property.Trim(), value ?? string.Empty);
        }

        /// <summary>Removes a style set through <see cref="SetStyle"/>; returns whether one was set.</summary>
        public bool ClearStyle(string property)
        {
            const string member = "UiElement:ClearStyle";
            BeginWrite(member);
            return !string.IsNullOrWhiteSpace(property) && Port.ClearStyle(property.Trim());
        }

        /// <summary>The resolved value of a longhand as USS text, or null (also when detached).</summary>
        public string GetStyle(string property)
        {
            return IsAlive && !string.IsNullOrWhiteSpace(property)
                ? Port.GetStyle(property.Trim().ToLowerInvariant())
                : null;
        }

        /// <summary>Sets a UXML attribute; returns false when it is not accepted (reported in the diagnostics).</summary>
        public bool SetAttribute(string name, string value)
        {
            const string member = "UiElement:SetAttribute";
            BeginWrite(member);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw RbxUiErrors.Create(RbxUiErrors.InvalidArgument, member + " expects an attribute name",
                    "pass a UXML attribute such as \"low-value\" or \"placeholder-text\"");
            }

            Screen.World.CheckText(value, member, "the value");
            return Port.SetAttribute(name.Trim(), value ?? string.Empty);
        }

        /// <summary>Sets the rows of a list view.</summary>
        public void SetItems(IReadOnlyList<string> items)
        {
            const string member = "UiElement:SetItems";
            BeginWrite(member);
            int total = 0;
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    total += items[i]?.Length ?? 0;
                }
            }

            if (total > Screen.World.Budgets.MaxTextLength)
            {
                throw RbxUiErrors.Create(RbxUiErrors.TextTooLarge,
                    member + " refused " + total + " characters of rows, at most "
                    + Screen.World.Budgets.MaxTextLength + " per call",
                    "page the list: show fewer rows at a time");
            }

            if (!Port.SetItems(items ?? Array.Empty<string>()))
            {
                throw Unsupported(member, "is not a list", "use a ListView for rows of text");
            }
        }

        /// <summary>Gives the element keyboard focus.</summary>
        public void Focus()
        {
            RequireAlive("UiElement:Focus");
            Port.Focus();
        }

        /// <summary>Takes keyboard focus away from the element.</summary>
        public void Blur()
        {
            RequireAlive("UiElement:Blur");
            Port.Blur();
        }

        /// <summary>The first descendant matching a selector, or null (also when detached).</summary>
        public RbxUiElementHandle Query(string selector)
        {
            const string member = "UiElement:Query";
            Screen.World.ValidateSelector(selector, member);
            return IsAlive ? Screen.Wrap(Port.Query(selector)) : null;
        }

        /// <summary>Creates a child element of a kind.</summary>
        public RbxUiElementHandle CreateChild(string kind, string name)
        {
            const string member = "UiElement:CreateChild";
            RequireAlive(member);
            return Screen.CreateElement(kind, this, name);
        }

        /// <summary>Removes the element and its subtree.</summary>
        public void Remove()
        {
            const string member = "UiElement:Remove";
            BeginWrite(member);
            if (!Port.Remove())
            {
                throw Unsupported(member, "cannot be removed", "the screen root stays; Destroy the screen instead");
            }

            Screen.AfterRebuild();
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Describe();
        }

        internal void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            if (_clicked != null)
            {
                Port.Clicked -= _onClicked;
            }

            if (_valueChanged != null)
            {
                Port.ValueChanged -= _onValueChanged;
            }

            if (_submitted != null)
            {
                Port.Submitted -= _onSubmitted;
            }
        }

        private void BeginWrite(string member)
        {
            RequireAlive(member);
            Screen.World.ChargeWrite(Screen.OwnerModId, member);
        }

        private RbxScriptSignal CreateSignal(string name, bool click, bool value, bool submit)
        {
            RbxScriptSignal signal = new(name);
            if (Screen.World.Scheduler != null)
            {
                signal.BindScheduler(Screen.World.Scheduler);
            }

            if (_released)
            {
                return signal;
            }

            if (click)
            {
                Port.Clicked += _onClicked;
            }

            if (value)
            {
                Port.ValueChanged += _onValueChanged;
            }

            if (submit)
            {
                Port.Submitted += _onSubmitted;
            }

            return signal;
        }

        private void OnClicked()
        {
            _clicked?.Fire();
        }

        private void OnValueChanged()
        {
            if (_valueChanged == null || !Port.TryGetValue(out RbxUiValue value))
            {
                return;
            }

            switch (value.Kind)
            {
                case RbxUiValueKind.Boolean:
                    _valueChanged.Fire(value.Boolean);
                    return;
                case RbxUiValueKind.Number:
                    _valueChanged.Fire(value.Number);
                    return;
                default:
                    _valueChanged.Fire(value.Text ?? string.Empty);
                    return;
            }
        }

        private void OnSubmitted()
        {
            _submitted?.Fire(Port.Text ?? string.Empty);
        }

        private string Describe()
        {
            string name = Port.Name;
            return Port.Kind + (string.IsNullOrEmpty(name) ? string.Empty : "#" + name) + " of screen '" + Screen.Id + "'";
        }

        private RbxError Unsupported(string member, string what, string fix)
        {
            return RbxUiErrors.Create(RbxUiErrors.UnsupportedOperation, member + ": " + Describe() + " " + what, fix);
        }

        private static string RequireClassName(string className, string member)
        {
            if (string.IsNullOrWhiteSpace(className))
            {
                throw RbxUiErrors.Create(RbxUiErrors.InvalidArgument, member + " expects a class name",
                    "pass a class such as \"is-selected\"");
            }

            return className.Trim();
        }

        private static string DescribeKind(RbxUiValueKind kind)
        {
            switch (kind)
            {
                case RbxUiValueKind.Boolean:
                    return "boolean";
                case RbxUiValueKind.Number:
                    return "number";
                case RbxUiValueKind.Text:
                    return "text";
                default:
                    return "nil";
            }
        }
    }
}
