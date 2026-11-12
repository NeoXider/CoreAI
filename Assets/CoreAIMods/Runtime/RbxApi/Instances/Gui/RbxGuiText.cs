using System;
using System.Text;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>A GUI object that draws text: <c>TextLabel</c>, <c>TextButton</c> and <c>TextBox</c>.</summary>
    public interface IRbxGuiTextObject
    {
        /// <summary>The text and its style.</summary>
        RbxGuiText TextState { get; }
    }

    /// <summary>
    /// The text members <c>TextLabel</c>, <c>TextButton</c> and <c>TextBox</c> share (Roblox declares them on each
    /// class): <c>Text</c>, <c>TextColor3</c>, <c>TextSize</c>, <c>TextTransparency</c>, <c>TextScaled</c>,
    /// <c>TextWrapped</c>, <c>TextXAlignment</c>, <c>TextYAlignment</c>, <c>TextTruncate</c>, <c>Font</c>,
    /// <c>RichText</c>, <c>TextStrokeColor3</c>, <c>TextStrokeTransparency</c> and <c>LineHeight</c>, with the
    /// <c>Full-API-Dump.json</c> defaults. <c>ContentText</c> is the text without rich-text tags.
    /// </summary>
    public sealed class RbxGuiText
    {
        /// <summary>Default text colour of <c>Full-API-Dump.json</c>: (0.105882, 0.164706, 0.207843).</summary>
        public static readonly RbxColor3 DefaultTextColor = new(0.105882f, 0.164706f, 0.207843f);

        /// <summary>Default text size of <c>Full-API-Dump.json</c>.</summary>
        public const float DefaultTextSize = 8f;

        private readonly RbxGuiObject _owner;
        private string _text;
        private RbxColor3 _textColor3 = DefaultTextColor;
        private float _textSize = DefaultTextSize;
        private float _textTransparency;
        private bool _textScaled;
        private bool _textWrapped;
        private int _textXAlignment = (int)RbxTextXAlignment.Center;
        private int _textYAlignment = (int)RbxTextYAlignment.Center;
        private int _textTruncate;
        private int _font;
        private bool _richText;
        private RbxColor3 _textStrokeColor3;
        private float _textStrokeTransparency = 1f;
        private float _lineHeight = 1f;
        private int _maxVisibleGraphemes = -1;
        private int _textDirection;

        internal RbxGuiText(RbxGuiObject owner, string defaultText)
        {
            _owner = owner;
            _text = defaultText;
        }

        public string Text => _text;

        public RbxColor3 TextColor3 => _textColor3;

        public float TextSize => _textSize;

        public float TextTransparency => _textTransparency;

        public bool TextScaled => _textScaled;

        public bool TextWrapped => _textWrapped;

        public RbxTextXAlignment TextXAlignment => (RbxTextXAlignment)_textXAlignment;

        public RbxTextYAlignment TextYAlignment => (RbxTextYAlignment)_textYAlignment;

        public RbxTextTruncate TextTruncate => (RbxTextTruncate)_textTruncate;

        /// <summary><c>Enum.Font</c> value (default Legacy).</summary>
        public int Font => _font;

        public bool RichText => _richText;

        public RbxColor3 TextStrokeColor3 => _textStrokeColor3;

        public float TextStrokeTransparency => _textStrokeTransparency;

        public float LineHeight => _lineHeight;

        public int MaxVisibleGraphemes => _maxVisibleGraphemes;

        /// <summary><c>Enum.TextDirection</c> value; recorded.</summary>
        public int TextDirection => _textDirection;

        /// <summary>Mirror <c>ContentText</c>: the text with rich-text tags removed when <see cref="RichText"/> is on.</summary>
        public string ContentText => _richText ? StripTags(_text) : _text;

        public void SetText(string value)
        {
            _owner.SetField(ref _text, value, "Text", RbxGuiChange.Text);
        }

        public void SetTextColor3(RbxColor3 value)
        {
            _owner.SetField(ref _textColor3, value, "TextColor3", RbxGuiChange.Text);
        }

        public void SetTextSize(double value)
        {
            _owner.SetField(ref _textSize, value, "TextSize", RbxGuiChange.Text);
        }

        public void SetTextTransparency(double value)
        {
            _owner.SetField(ref _textTransparency, value, "TextTransparency", RbxGuiChange.Text);
        }

        public void SetTextScaled(bool value)
        {
            _owner.SetField(ref _textScaled, value, "TextScaled", RbxGuiChange.Text);
        }

        public void SetTextWrapped(bool value)
        {
            _owner.SetField(ref _textWrapped, value, "TextWrapped", RbxGuiChange.Text);
        }

        public void SetTextXAlignment(RbxTextXAlignment value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 2, _owner.ClassName, "TextXAlignment");
            _owner.SetField(ref _textXAlignment, (int)value, "TextXAlignment", RbxGuiChange.Text);
        }

        public void SetTextYAlignment(RbxTextYAlignment value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 2, _owner.ClassName, "TextYAlignment");
            _owner.SetField(ref _textYAlignment, (int)value, "TextYAlignment", RbxGuiChange.Text);
        }

        public void SetTextTruncate(RbxTextTruncate value)
        {
            RbxGuiValidation.RequireRange((int)value, 0, 2, _owner.ClassName, "TextTruncate");
            _owner.SetField(ref _textTruncate, (int)value, "TextTruncate", RbxGuiChange.Text);
        }

        public void SetFont(int value)
        {
            _owner.SetField(ref _font, value, "Font", RbxGuiChange.Text);
        }

        public void SetRichText(bool value)
        {
            _owner.SetField(ref _richText, value, "RichText", RbxGuiChange.Text);
        }

        public void SetTextStrokeColor3(RbxColor3 value)
        {
            _owner.SetField(ref _textStrokeColor3, value, "TextStrokeColor3", RbxGuiChange.Text);
        }

        public void SetTextStrokeTransparency(double value)
        {
            _owner.SetField(ref _textStrokeTransparency, value, "TextStrokeTransparency", RbxGuiChange.Text);
        }

        public void SetLineHeight(double value)
        {
            _owner.SetField(ref _lineHeight, value, "LineHeight", RbxGuiChange.Text);
        }

        public void SetMaxVisibleGraphemes(int value)
        {
            _owner.SetField(ref _maxVisibleGraphemes, value, "MaxVisibleGraphemes", RbxGuiChange.None);
        }

        public void SetTextDirection(int value)
        {
            _owner.SetField(ref _textDirection, value, "TextDirection", RbxGuiChange.None);
        }

        /// <summary>
        /// Stores text the player typed into a <c>TextBox</c>: the field already shows it, so only the property
        /// signals fire and nothing is pushed back to the screen.
        /// </summary>
        internal bool ApplyTypedText(string value)
        {
            string next = value ?? string.Empty;
            if (string.Equals(_text, next, StringComparison.Ordinal) || _owner.IsDestroyed)
            {
                return false;
            }

            _text = next;
            _owner.AfterWrite("Text", RbxGuiChange.None);
            return true;
        }

        /// <summary>Bold and italic by font name: the faces CoreAI draws (every family renders in the host font).</summary>
        public bool IsBold
        {
            get
            {
                switch (_font)
                {
                    case 2:
                    case 4:
                    case 16:
                    case 19:
                    case 20:
                    case 48:
                    case 49:
                    case 51:
                        return true;
                    default:
                        return false;
                }
            }
        }

        /// <summary>True for the italic faces of <c>Enum.Font</c>.</summary>
        public bool IsItalic => _font == 6;

        internal void CopyTo(RbxGuiText copy)
        {
            copy._text = _text;
            copy._textColor3 = _textColor3;
            copy._textSize = _textSize;
            copy._textTransparency = _textTransparency;
            copy._textScaled = _textScaled;
            copy._textWrapped = _textWrapped;
            copy._textXAlignment = _textXAlignment;
            copy._textYAlignment = _textYAlignment;
            copy._textTruncate = _textTruncate;
            copy._font = _font;
            copy._richText = _richText;
            copy._textStrokeColor3 = _textStrokeColor3;
            copy._textStrokeTransparency = _textStrokeTransparency;
            copy._lineHeight = _lineHeight;
            copy._maxVisibleGraphemes = _maxVisibleGraphemes;
            copy._textDirection = _textDirection;
        }

        private static string StripTags(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0)
            {
                return text;
            }

            StringBuilder builder = new(text.Length);
            bool inTag = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<')
                {
                    inTag = true;
                    continue;
                }

                if (c == '>' && inTag)
                {
                    inTag = false;
                    continue;
                }

                if (!inTag)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }

    /// <summary>Roblox <c>GuiLabel</c>: the abstract base of the labels.</summary>
    public abstract class RbxGuiLabel : RbxGuiObject
    {
        private protected RbxGuiLabel(ClassDescriptor descriptor, RbxUDim2 defaultSize)
            : base(descriptor, defaultSize, false)
        {
        }
    }

    /// <summary>Roblox <c>TextLabel</c>: a box with text (default 200 by 50 pixels, text "Label").</summary>
    public sealed class RbxTextLabel : RbxGuiLabel, IRbxGuiTextObject
    {
        internal RbxTextLabel(ClassDescriptor descriptor)
            : base(descriptor, new RbxUDim2(0f, 200, 0f, 50))
        {
            TextState = new RbxGuiText(this, "Label");
        }

        /// <inheritdoc />
        public RbxGuiText TextState { get; }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            TextState.CopyTo(((RbxTextLabel)copy).TextState);
        }
    }

    /// <summary>Roblox <c>TextButton</c>: a button with text (default 200 by 50 pixels, text "Button").</summary>
    public sealed class RbxTextButton : RbxGuiButton, IRbxGuiTextObject
    {
        internal RbxTextButton(ClassDescriptor descriptor)
            : base(descriptor, new RbxUDim2(0f, 200, 0f, 50))
        {
            TextState = new RbxGuiText(this, "Button");
        }

        /// <inheritdoc />
        public RbxGuiText TextState { get; }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            TextState.CopyTo(((RbxTextButton)copy).TextState);
        }
    }

    /// <summary>
    /// Roblox <c>TextBox</c>: a text field (default 200 by 50 pixels, text "TextBox"). Typing updates
    /// <c>Text</c> and fires its property signals; <c>Focused</c> and <c>FocusLost(enterPressed)</c> fire as
    /// focus moves; while it is focused, keyboard input is processed by the GUI, so the character does not move.
    /// <c>ClearTextOnFocus</c> (default true) empties the text when it gains focus.
    /// </summary>
    public sealed class RbxTextBox : RbxGuiObject, IRbxGuiTextObject
    {
        /// <summary>Default placeholder colour of <c>Full-API-Dump.json</c>.</summary>
        public static readonly RbxColor3 DefaultPlaceholderColor = new(0.5f, 0.5f, 0.5f);

        private bool _clearTextOnFocus = true;
        private bool _multiLine;
        private string _placeholderText = string.Empty;
        private RbxColor3 _placeholderColor3 = DefaultPlaceholderColor;
        private bool _textEditable = true;
        private int _cursorPosition = 1;
        private bool _showNativeInput = true;

        internal RbxTextBox(ClassDescriptor descriptor)
            : base(descriptor, new RbxUDim2(0f, 200, 0f, 50), true)
        {
            TextState = new RbxGuiText(this, "TextBox");
        }

        /// <inheritdoc />
        public RbxGuiText TextState { get; }

        public bool ClearTextOnFocus => _clearTextOnFocus;

        public bool MultiLine => _multiLine;

        public string PlaceholderText => _placeholderText;

        public RbxColor3 PlaceholderColor3 => _placeholderColor3;

        public bool TextEditable => _textEditable;

        /// <summary>Recorded; the host field keeps its own caret.</summary>
        public int CursorPosition => _cursorPosition;

        /// <summary>Recorded.</summary>
        public bool ShowNativeInput => _showNativeInput;

        /// <summary>True while the text box holds keyboard focus.</summary>
        public bool IsFocused { get; private set; }

        /// <summary>Mirror <c>TextBox.Focused()</c>.</summary>
        public RbxScriptSignal Focused => GetOrCreateSignal("Focused");

        /// <summary>Mirror <c>TextBox.FocusLost(enterPressed, inputThatCausedFocusLoss)</c>.</summary>
        public RbxScriptSignal FocusLost => GetOrCreateSignal("FocusLost");

        /// <summary>Set by the presenter: moves keyboard focus into or out of the field on screen.</summary>
        internal Action<RbxTextBox, bool> FocusRequest { get; set; }

        public void SetClearTextOnFocus(bool value)
        {
            SetField(ref _clearTextOnFocus, value, nameof(ClearTextOnFocus), RbxGuiChange.None);
        }

        public void SetMultiLine(bool value)
        {
            SetField(ref _multiLine, value, nameof(MultiLine), RbxGuiChange.Field);
        }

        public void SetPlaceholderText(string value)
        {
            SetField(ref _placeholderText, value, nameof(PlaceholderText), RbxGuiChange.Field);
        }

        public void SetPlaceholderColor3(RbxColor3 value)
        {
            SetField(ref _placeholderColor3, value, nameof(PlaceholderColor3), RbxGuiChange.Field);
        }

        public void SetTextEditable(bool value)
        {
            SetField(ref _textEditable, value, nameof(TextEditable), RbxGuiChange.Field);
        }

        public void SetCursorPosition(int value)
        {
            SetField(ref _cursorPosition, value, nameof(CursorPosition), RbxGuiChange.None);
        }

        public void SetShowNativeInput(bool value)
        {
            SetField(ref _showNativeInput, value, nameof(ShowNativeInput), RbxGuiChange.None);
        }

        /// <summary>
        /// Mirror <c>TextBox:CaptureFocus()</c>: the field on screen takes keyboard focus, with the caret after the
        /// text. A box shown or created in the same frame takes it once the frame's GUI update has drawn it, and
        /// the keystroke being pressed when the script asks does not type into it (DEV-141).
        /// </summary>
        public void CaptureFocus()
        {
            ThrowIfDestroyed("CaptureFocus");
            RequestFocus(true);
        }

        private void RequestFocus(bool focus)
        {
            if (FocusRequest != null)
            {
                FocusRequest(this, focus);
            }
            else
            {
                // WHY through the registry: a box made in the same handler has no element yet, so no presenter
                // has wired it; the world's presenter still takes the request and applies it after its update.
                Registry?.GuiLayout?.RequestFocus(this, focus);
            }
        }

        /// <summary>Mirror <c>TextBox:ReleaseFocus(submitted)</c>: focus leaves; FocusLost carries <paramref name="submitted"/>.</summary>
        public void ReleaseFocus(bool submitted)
        {
            ThrowIfDestroyed("ReleaseFocus");
            if (!IsFocused)
            {
                // WHY told anyway: a CaptureFocus of this frame may still wait for the GUI update; it is dropped.
                RequestFocus(false);
                return;
            }

            PendingEnterPressed = submitted;
            RequestFocus(false);
        }

        /// <summary>The enterPressed value the next FocusLost carries.</summary>
        internal bool PendingEnterPressed { get; set; }

        /// <summary>The field gained focus on screen.</summary>
        internal void OnFocusGained()
        {
            if (IsDestroyed || IsFocused)
            {
                return;
            }

            IsFocused = true;
            if (_clearTextOnFocus)
            {
                TextState.ApplyTypedText(string.Empty);
            }

            if (HasSignalConnections("Focused"))
            {
                FireSignal("Focused");
            }
        }

        /// <summary>The field lost focus on screen; FocusLost carries whether Enter caused it.</summary>
        internal void OnFocusLost(object input)
        {
            if (IsDestroyed || !IsFocused)
            {
                return;
            }

            IsFocused = false;
            bool enterPressed = PendingEnterPressed;
            PendingEnterPressed = false;
            if (HasSignalConnections("FocusLost"))
            {
                FireSignal("FocusLost", enterPressed, input);
            }
        }

        /// <inheritdoc />
        protected internal override void CopyCustomStateTo(RbxInstance copy)
        {
            base.CopyCustomStateTo(copy);
            RbxTextBox box = (RbxTextBox)copy;
            TextState.CopyTo(box.TextState);
            box._clearTextOnFocus = _clearTextOnFocus;
            box._multiLine = _multiLine;
            box._placeholderText = _placeholderText;
            box._placeholderColor3 = _placeholderColor3;
            box._textEditable = _textEditable;
            box._cursorPosition = _cursorPosition;
            box._showNativeInput = _showNativeInput;
        }
    }
}
