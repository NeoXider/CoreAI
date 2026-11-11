using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// The engine-free port the <c>UxmlService</c> Lua surface drives: a runtime UI host that builds
    /// screens from UXML and USS text. The Unity layer implements it over <c>IRuntimeUiHost</c>
    /// (<c>RuntimeUiHostBackend</c>); a host game or a test replaces it with its own implementation.
    /// </summary>
    /// <remarks>
    /// WHY a port and not <c>IRuntimeUiHost</c> itself: that contract lives in an engine assembly and
    /// hands out sealed, engine-bound screen and element classes, so no fake could stand behind it and
    /// the Lua surface could not be tested without the engine. This port is the narrow subset Lua
    /// needs, adapted by one class.
    /// </remarks>
    public interface IRbxUiBackend
    {
        /// <summary>False when the host game switched runtime UI off: every screen call then fails with <c>UI_DISABLED</c>.</summary>
        bool IsEnabled { get; }

        /// <summary>True while a text field of any runtime screen holds keyboard focus.</summary>
        bool IsTextInputFocused { get; }

        /// <summary>True while the pointer is over a pickable element of any runtime screen.</summary>
        bool IsPointerOverUi { get; }

        /// <summary>Raised with the new value whenever <see cref="IsTextInputFocused"/> changes.</summary>
        event Action<bool> TextInputFocusChanged;

        /// <summary>Raised with the new value whenever <see cref="IsPointerOverUi"/> changes.</summary>
        event Action<bool> PointerOverUiChanged;

        /// <summary>Builds a screen from UXML and USS text (either may be null or empty).</summary>
        /// <param name="hostId">Id unique in the host; the Lua surface namespaces it per mod.</param>
        /// <param name="owner">Owner tag kept by the host for bookkeeping (the mod id).</param>
        RbxUiResult CreateScreen(string hostId, string owner, string uxml, string uss, int sortOrder, bool visible);

        /// <summary>How many authored elements <paramref name="uxml"/> builds (templates expanded); used to enforce budgets before a build.</summary>
        int CountElements(string uxml);

        /// <summary>True when <paramref name="selector"/> is in the supported selector grammar.</summary>
        bool IsValidSelector(string selector, out string error);

        /// <summary>The host theme's value of a design token, or null when the theme does not define it.</summary>
        string GetThemeToken(string name);
    }

    /// <summary>One screen of an <see cref="IRbxUiBackend"/>.</summary>
    public interface IRbxUiScreen
    {
        /// <summary>True after the screen was destroyed (by anyone).</summary>
        bool IsDestroyed { get; }

        /// <summary>True while shown.</summary>
        bool IsVisible { get; }

        /// <summary>Draw order against other screens; higher is on top.</summary>
        int SortOrder { get; }

        /// <summary>Authored elements (markup and API-created), the root excluded.</summary>
        int ElementCount { get; }

        /// <summary>Reports of the last replace plus API reports since.</summary>
        IReadOnlyList<RbxUiDiagnostic> Diagnostics { get; }

        /// <summary>The screen root element (styled by <c>:root</c>), or null once destroyed.</summary>
        IRbxUiElement Root { get; }

        /// <summary>Raised once when the screen is destroyed.</summary>
        event Action Destroyed;

        /// <summary>The width of the screen root in panel pixels after layout; 0 before the first layout.</summary>
        float Width { get; }

        /// <summary>The height of the screen root in panel pixels after layout; 0 before the first layout.</summary>
        float Height { get; }

        /// <summary>Raised when <see cref="Width"/> or <see cref="Height"/> changes (the panel was resized).</summary>
        event Action Resized;

        /// <summary>Rebuilds the tree from new UXML; values and handles of matching names carry over.</summary>
        RbxUiResult ReplaceUxml(string uxml);

        /// <summary>Restyles the same tree from new USS.</summary>
        RbxUiResult ReplaceUss(string uss);

        /// <summary>Replaces both texts in one rebuild.</summary>
        RbxUiResult Replace(string uxml, string uss);

        /// <summary>Shows or hides the screen.</summary>
        void SetVisible(bool visible);

        /// <summary>Changes the draw order.</summary>
        void SetSortOrder(int sortOrder);

        /// <summary>The first authored element matching a selector, or null.</summary>
        IRbxUiElement Query(string selector);

        /// <summary>Appends every authored element matching a selector, in document order.</summary>
        void QueryAll(string selector, List<IRbxUiElement> results);

        /// <summary>The first authored element with a name, or null.</summary>
        IRbxUiElement GetElement(string name);

        /// <summary>Creates an element of a UXML type name under <paramref name="parent"/> (the root when null); null when refused (reported).</summary>
        IRbxUiElement CreateElement(string kind, IRbxUiElement parent, string name);

        /// <summary>Destroys the screen and everything attached to it.</summary>
        void Destroy();
    }

    /// <summary>A stable handle on one element of an <see cref="IRbxUiScreen"/>.</summary>
    public interface IRbxUiElement
    {
        /// <summary>True while the handle points at a live element.</summary>
        bool IsAlive { get; }

        /// <summary>The element name, or null.</summary>
        string Name { get; }

        /// <summary>The UXML type name (<c>Button</c>, <c>Label</c>, ...).</summary>
        string Kind { get; }

        /// <summary>The parent element, or null at the screen root.</summary>
        IRbxUiElement Parent { get; }

        /// <summary>The text (label, button, toggle, field value, progress title, slider label), or null.</summary>
        string Text { get; }

        /// <summary>True when the element is displayed.</summary>
        bool Visible { get; }

        /// <summary>True when the element and its ancestors are enabled.</summary>
        bool Enabled { get; }

        /// <summary>Stacking order among siblings.</summary>
        int ZOrder { get; }

        /// <summary>Raised when the element is clicked (a button also on keyboard or gamepad submit).</summary>
        event Action Clicked;

        /// <summary>Raised when the player changes the value.</summary>
        event Action ValueChanged;

        /// <summary>Raised when Enter is pressed in a single-line text field.</summary>
        event Action Submitted;

        /// <summary>Raised when the pointer enters the element (it must take the pointer: picking-mode Position).</summary>
        event Action PointerEntered;

        /// <summary>Raised when the pointer leaves the element.</summary>
        event Action PointerExited;

        /// <summary>Raised when keyboard focus moves into the element.</summary>
        event Action FocusGained;

        /// <summary>Raised when keyboard focus leaves the element.</summary>
        event Action FocusLost;

        /// <summary>
        /// Raised when a scroll view's offset changes by anything but <see cref="SetScrollOffset"/>: the player's
        /// wheel, drag or scroll bar, or the view clamping the offset to its content.
        /// </summary>
        event Action ScrollChanged;

        /// <summary>How far a scroll view's content is scrolled to the left, in pixels; 0 for any other kind.</summary>
        float ScrollOffsetX { get; }

        /// <summary>How far a scroll view's content is scrolled up, in pixels; 0 for any other kind.</summary>
        float ScrollOffsetY { get; }

        /// <summary>
        /// Scrolls a scroll view (the view clamps the offset to its content) without raising
        /// <see cref="ScrollChanged"/>; false when detached, not a scroll view, or not finite.
        /// </summary>
        bool SetScrollOffset(float x, float y);

        /// <summary>
        /// Sets one longhand from a typed value without parsing text: the per-frame safe path. It wins over every
        /// rule like <see cref="SetStyle(string, string)"/>; false when detached or the value does not fit.
        /// </summary>
        bool SetStyle(RbxUiStyleProperty property, RbxUiStyleValue value);

        /// <summary>Sets the text; false when detached or the kind has no text.</summary>
        bool SetText(string text);

        /// <summary>Reads the value: a toggle's boolean, a slider's, progress bar's or list's number, a field's text.</summary>
        bool TryGetValue(out RbxUiValue value);

        /// <summary>Sets the value without raising <see cref="ValueChanged"/>; false when the kind has none or the value does not fit.</summary>
        bool SetValue(RbxUiValue value);

        /// <summary>Shows or hides the element; false when detached.</summary>
        bool SetVisible(bool visible);

        /// <summary>Enables or disables the element and its subtree; false when detached.</summary>
        bool SetEnabled(bool enabled);

        /// <summary>Sets the stacking order among siblings; false when detached.</summary>
        bool SetZOrder(int zOrder);

        /// <summary>Adds a class; false when detached or the name is blank.</summary>
        bool AddClass(string className);

        /// <summary>Removes a class; false when detached or the name is blank.</summary>
        bool RemoveClass(string className);

        /// <summary>Adds the class when absent, removes it when present.</summary>
        bool ToggleClass(string className);

        /// <summary>True when the element carries the class.</summary>
        bool HasClass(string className);

        /// <summary>Copies the element's classes into <paramref name="output"/> (cleared first).</summary>
        void GetClasses(List<string> output);

        /// <summary>Sets one style property from USS text; false when unsupported or invalid (reported in the screen's diagnostics).</summary>
        bool SetStyle(string property, string value);

        /// <summary>Removes a style set through <see cref="SetStyle"/>.</summary>
        bool ClearStyle(string property);

        /// <summary>The resolved value of a longhand as USS text, or null.</summary>
        string GetStyle(string property);

        /// <summary>Sets a UXML attribute; false when the attribute or value is not accepted (reported).</summary>
        bool SetAttribute(string name, string value);

        /// <summary>Sets the rows of a list view; false for any other kind.</summary>
        bool SetItems(IReadOnlyList<string> items);

        /// <summary>Gives the element keyboard focus.</summary>
        bool Focus();

        /// <summary>Takes keyboard focus away from the element.</summary>
        bool Blur();

        /// <summary>The first authored descendant matching a selector, or null.</summary>
        IRbxUiElement Query(string selector);

        /// <summary>Creates a child element of a UXML type name; null when refused (reported).</summary>
        IRbxUiElement CreateChild(string kind, string name);

        /// <summary>Removes the element and its subtree.</summary>
        bool Remove();
    }

    /// <summary>What an element value is.</summary>
    public enum RbxUiValueKind
    {
        /// <summary>No value.</summary>
        None = 0,

        /// <summary>A boolean (toggle).</summary>
        Boolean = 1,

        /// <summary>A number (slider, progress bar, list selection).</summary>
        Number = 2,

        /// <summary>Text (text field).</summary>
        Text = 3
    }

    /// <summary>An element value as a plain value: a boolean, a number or text.</summary>
    public readonly struct RbxUiValue
    {
        private RbxUiValue(RbxUiValueKind kind, bool boolean, double number, string text)
        {
            Kind = kind;
            Boolean = boolean;
            Number = number;
            Text = text;
        }

        /// <summary>Which field holds the value.</summary>
        public RbxUiValueKind Kind { get; }

        /// <summary>The boolean when <see cref="Kind"/> is <see cref="RbxUiValueKind.Boolean"/>.</summary>
        public bool Boolean { get; }

        /// <summary>The number when <see cref="Kind"/> is <see cref="RbxUiValueKind.Number"/>.</summary>
        public double Number { get; }

        /// <summary>The text when <see cref="Kind"/> is <see cref="RbxUiValueKind.Text"/>.</summary>
        public string Text { get; }

        /// <summary>A boolean value.</summary>
        public static RbxUiValue FromBoolean(bool value)
        {
            return new RbxUiValue(RbxUiValueKind.Boolean, value, 0d, null);
        }

        /// <summary>A number value.</summary>
        public static RbxUiValue FromNumber(double value)
        {
            return new RbxUiValue(RbxUiValueKind.Number, false, value, null);
        }

        /// <summary>A text value.</summary>
        public static RbxUiValue FromText(string value)
        {
            return new RbxUiValue(RbxUiValueKind.Text, false, 0d, value ?? string.Empty);
        }

        /// <summary>The value as text, the way a USS attribute would spell it.</summary>
        public string ToText()
        {
            switch (Kind)
            {
                case RbxUiValueKind.Boolean:
                    return Boolean ? "true" : "false";
                case RbxUiValueKind.Number:
                    return Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                case RbxUiValueKind.Text:
                    return Text;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// One report from the UXML or USS interpreter or the host, as plain data. Reports are returned to
    /// the script, never raised, so the author (or the AI) can repair the source from them.
    /// </summary>
    public readonly struct RbxUiDiagnostic
    {
        /// <summary>Creates a report.</summary>
        public RbxUiDiagnostic(string code, string severity, string source, int line, int column, string message)
        {
            Code = code ?? string.Empty;
            Severity = severity ?? string.Empty;
            Source = source ?? string.Empty;
            Line = line;
            Column = column;
            Message = message ?? string.Empty;
        }

        /// <summary>Stable code (<c>USS_UNSUPPORTED_PROPERTY</c>, ...).</summary>
        public string Code { get; }

        /// <summary><c>Info</c>, <c>Warning</c> or <c>Error</c>.</summary>
        public string Severity { get; }

        /// <summary><c>uxml</c>, <c>uss</c>, <c>style</c> or <c>api</c>.</summary>
        public string Source { get; }

        /// <summary>1-based line, or 0.</summary>
        public int Line { get; }

        /// <summary>1-based column, or 0.</summary>
        public int Column { get; }

        /// <summary>Human-readable explanation.</summary>
        public string Message { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            return Source + ":" + Line + ":" + Column + " " + Severity + " " + Code + ": " + Message;
        }
    }

    /// <summary>The outcome of a backend screen operation.</summary>
    public sealed class RbxUiResult
    {
        private static readonly IReadOnlyList<RbxUiDiagnostic> NoDiagnostics = Array.Empty<RbxUiDiagnostic>();

        private RbxUiResult(bool success, string errorCode, string error, IRbxUiScreen screen,
            IReadOnlyList<RbxUiDiagnostic> diagnostics)
        {
            Success = success;
            ErrorCode = errorCode;
            Error = error;
            Screen = screen;
            Diagnostics = diagnostics ?? NoDiagnostics;
        }

        /// <summary>True when the operation took effect.</summary>
        public bool Success { get; }

        /// <summary>A stable <c>UI_*</c> code when <see cref="Success"/> is false.</summary>
        public string ErrorCode { get; }

        /// <summary>Why it failed.</summary>
        public string Error { get; }

        /// <summary>The screen created or changed, when there is one.</summary>
        public IRbxUiScreen Screen { get; }

        /// <summary>Reports raised by the operation.</summary>
        public IReadOnlyList<RbxUiDiagnostic> Diagnostics { get; }

        /// <summary>A success.</summary>
        public static RbxUiResult Ok(IRbxUiScreen screen, IReadOnlyList<RbxUiDiagnostic> diagnostics)
        {
            return new RbxUiResult(true, null, null, screen, diagnostics);
        }

        /// <summary>A failure with a stable code.</summary>
        public static RbxUiResult Fail(string errorCode, string error,
            IReadOnlyList<RbxUiDiagnostic> diagnostics = null)
        {
            return new RbxUiResult(false, errorCode, error, null, diagnostics);
        }
    }
}
