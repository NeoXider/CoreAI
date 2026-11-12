using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// Default UI Toolkit view of the in-world chat: a feed of fading lines, a one-line input and the agent status
    /// line, bottom-left over the running game. The tree is built in code once; <see cref="Attach"/> puts it into
    /// a host element and binds the input callbacks, <see cref="Detach"/> unbinds them first, so a host rebuild
    /// never stacks handlers. While the input is closed nothing in the overlay takes the pointer.
    /// </summary>
    public sealed class CoreAiChatOverlayView : IChatOverlayView
    {
        /// <summary>Class of the overlay root.</summary>
        public const string RootClassName = "coreai-chat-overlay";

        /// <summary>Added to the root while the input is open.</summary>
        public const string OpenClassName = "coreai-chat-overlay--open";

        /// <summary>Class of the column that holds feed, input and status.</summary>
        public const string ColumnClassName = "coreai-chat-overlay__column";

        /// <summary>Class of the feed scroll view.</summary>
        public const string FeedClassName = "coreai-chat-overlay__feed";

        /// <summary>Class of one feed line.</summary>
        public const string LineClassName = "coreai-chat-overlay__line";

        /// <summary>Added to a line while it is faded out.</summary>
        public const string LineFadedClassName = "coreai-chat-overlay__line--faded";

        /// <summary>Class of a line's speaker label.</summary>
        public const string SpeakerClassName = "coreai-chat-overlay__speaker";

        /// <summary>Class of a line's text label.</summary>
        public const string TextClassName = "coreai-chat-overlay__text";

        /// <summary>Class of the input field.</summary>
        public const string InputClassName = "coreai-chat-overlay__input";

        /// <summary>Class of the status line.</summary>
        public const string StatusClassName = "coreai-chat-overlay__status";

        private static readonly Color PanelBackground = new(0f, 0f, 0f, 0.45f);
        private static readonly Color InputBackground = new(0f, 0f, 0f, 0.7f);
        private static readonly Color InputBorder = new(1f, 1f, 1f, 0.35f);
        private static readonly Color InputSelection = new(1f, 1f, 1f, 0.3f);

        private readonly CoreAiChatOverlaySettings _settings;
        private readonly ChatOverlayOptions _options;
        private readonly VisualElement _root;
        private readonly VisualElement _column;
        private readonly ScrollView _feed;
        private readonly TextField _input;
        private readonly Label _status;
        private readonly Dictionary<long, LineElements> _lines = new();
        private VisualElement _host;
        private bool _callbacksBound;
        private bool _inputOpen;
        private bool _stickToBottom = true;
        private bool _scrollScheduled;
        private KeyCode _suppressKey = KeyCode.None;
        private char _suppressCharacter;
        private bool _suppressNextCharacter;
        private int _suppressUntilFrame = -1;
        private int _lastSubmitFrame = -1;

        /// <summary>Builds the overlay tree; nothing is shown until <see cref="Attach"/>.</summary>
        public CoreAiChatOverlayView(CoreAiChatOverlaySettings settings = null, ChatOverlayOptions options = null)
        {
            _settings = settings ?? new CoreAiChatOverlaySettings();
            _options = options ?? _settings.ResolveOptions();

            _root = new VisualElement { name = "coreai-chat-overlay", pickingMode = PickingMode.Ignore };
            _root.AddToClassList(RootClassName);
            _column = new VisualElement { name = "coreai-chat-overlay-column", pickingMode = PickingMode.Ignore };
            _column.AddToClassList(ColumnClassName);
            _feed = new ScrollView(ScrollViewMode.Vertical) { name = "coreai-chat-overlay-feed", focusable = false };
            _feed.AddToClassList(FeedClassName);
            _feed.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _feed.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _input = new TextField { name = "coreai-chat-overlay-input", multiline = false };
            _input.AddToClassList(InputClassName);
            _input.maxLength = Math.Max(1, _options.MaxInputLength);
            _input.selectAllOnFocus = false;
            _input.selectAllOnMouseUp = false;
            _input.textEdition.placeholder = _options.WithPrefix(_options.InputPlaceholder);
            _status = new Label { name = "coreai-chat-overlay-status", pickingMode = PickingMode.Ignore };
            _status.AddToClassList(StatusClassName);

            _column.Add(_feed);
            _column.Add(_input);
            _column.Add(_status);
            _root.Add(_column);
            if (_settings.StyleSheet != null)
            {
                _root.styleSheets.Add(_settings.StyleSheet);
            }

            if (_settings.UseDefaultStyle)
            {
                ApplyDefaultStyle();
            }

            _input.style.display = DisplayStyle.None;
            _status.style.display = DisplayStyle.None;
            SetFeedInteractive(false);
        }

        /// <inheritdoc />
        public event Action<string> Submitted;

        /// <inheritdoc />
        public event Action CloseRequested;

        /// <summary>The overlay root element (attached or not).</summary>
        public VisualElement Root => _root;

        /// <summary>The element the overlay is attached to; null when detached.</summary>
        public VisualElement Host => _host;

        /// <summary>The input field.</summary>
        public TextField Input => _input;

        /// <summary>The status line.</summary>
        public Label StatusLabel => _status;

        /// <summary>The feed scroll view.</summary>
        public ScrollView Feed => _feed;

        /// <summary>True while the input is shown.</summary>
        public bool IsInputOpen => _inputOpen;

        /// <summary>Number of lines currently rendered.</summary>
        public int LineCount => _lines.Count;

        /// <summary>Puts the overlay into <paramref name="host"/> and binds its callbacks; re-attaching moves it.</summary>
        public void Attach(VisualElement host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (ReferenceEquals(_host, host) && _root.parent == host)
            {
                return;
            }

            Detach();
            _host = host;
            host.Add(_root);
            BindCallbacks();
            if (_inputOpen)
            {
                FocusInputAtEnd();
            }

            ScheduleScrollToBottom();
        }

        /// <summary>Unbinds the callbacks and removes the overlay from its host.</summary>
        public void Detach()
        {
            UnbindCallbacks();
            _root.RemoveFromHierarchy();
            _host = null;
            _scrollScheduled = false;
        }

        /// <summary>
        /// Swallows the keystroke that opened the input (its key code and its character) until the frame after
        /// <paramref name="frame"/>, so the opening key never lands in the field. The character is swallowed
        /// whatever the keyboard layout made of it.
        /// </summary>
        public void SuppressOpeningKey(KeyCode key, int frame)
        {
            _suppressKey = key;
            _suppressCharacter = ChatOverlayHotkeys.CharacterFor(key);
            _suppressNextCharacter = _suppressCharacter != '\0';
            _suppressUntilFrame = frame + 1;
        }

        /// <inheritdoc />
        public void SetInputOpen(bool open, string prefill)
        {
            _inputOpen = open;
            _root.EnableInClassList(OpenClassName, open);
            if (open)
            {
                _input.style.display = DisplayStyle.Flex;
                _input.SetValueWithoutNotify(prefill ?? "");
                SetFeedInteractive(true);
                FocusInputAtEnd();
            }
            else
            {
                _input.SetValueWithoutNotify("");
                _input.Blur();
                _input.style.display = DisplayStyle.None;
                SetFeedInteractive(false);
                _stickToBottom = true;
            }

            ScheduleScrollToBottom();
        }

        /// <inheritdoc />
        public void AddLine(ChatFeedLine line)
        {
            if (line == null || _lines.ContainsKey(line.Id))
            {
                return;
            }

            VisualElement row = new() { pickingMode = PickingMode.Ignore };
            row.AddToClassList(LineClassName);
            row.AddToClassList(LineClassName + "--" + KindName(line.Kind));
            Label speaker = new() { pickingMode = PickingMode.Ignore, enableRichText = false };
            speaker.AddToClassList(SpeakerClassName);
            Label text = new() { pickingMode = PickingMode.Ignore, enableRichText = false };
            text.AddToClassList(TextClassName);
            row.Add(speaker);
            row.Add(text);
            if (_settings.UseDefaultStyle)
            {
                StyleLine(row, speaker, text, line.Kind);
            }

            LineElements elements = new(row, speaker, text);
            _lines[line.Id] = elements;
            _feed.Add(row);
            Render(line, elements);
            ScheduleScrollToBottom();
        }

        /// <inheritdoc />
        public void UpdateLine(ChatFeedLine line)
        {
            if (line != null && _lines.TryGetValue(line.Id, out LineElements elements))
            {
                Render(line, elements);
                ScheduleScrollToBottom();
            }
        }

        /// <inheritdoc />
        public void RemoveLine(ChatFeedLine line)
        {
            if (line != null && _lines.TryGetValue(line.Id, out LineElements elements))
            {
                elements.Row.RemoveFromHierarchy();
                _lines.Remove(line.Id);
            }
        }

        /// <inheritdoc />
        public void SetLineVisible(ChatFeedLine line, bool visible)
        {
            if (line == null || !_lines.TryGetValue(line.Id, out LineElements elements))
            {
                return;
            }

            elements.Row.EnableInClassList(LineFadedClassName, !visible);
            if (_settings.UseDefaultStyle)
            {
                elements.Row.style.opacity = visible ? 1f : 0f;
            }
        }

        /// <inheritdoc />
        public void SetStatus(AgentStatusKind kind, string text)
        {
            bool show = !string.IsNullOrEmpty(text);
            _status.text = text ?? "";
            _status.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (AgentStatusKind value in StatusKinds)
            {
                _status.EnableInClassList(StatusClassName + "--" + KindName(value), show && value == kind);
            }
        }

        /// <summary>True when the line with <paramref name="lineId"/> is rendered and not faded.</summary>
        public bool IsLineShown(long lineId)
        {
            return _lines.TryGetValue(lineId, out LineElements elements) &&
                   !elements.Row.ClassListContains(LineFadedClassName);
        }

        /// <summary>The rendered text of the line with <paramref name="lineId"/>; null when it is not rendered.</summary>
        public string GetLineText(long lineId)
        {
            return _lines.TryGetValue(lineId, out LineElements elements) ? elements.Text.text : null;
        }

        /// <summary>
        /// The key handling of the input field without the event: swallows the opening keystroke, turns Escape
        /// into <see cref="CloseRequested"/> and Enter into <see cref="Submitted"/>. Returns true when the key was
        /// consumed. <paramref name="frame"/> is the frame the key arrived in.
        /// </summary>
        internal bool HandleInputKey(KeyCode keyCode, char character, int frame)
        {
            if (!_inputOpen)
            {
                return false;
            }

            if (frame <= _suppressUntilFrame && MatchesSuppressed(keyCode, character))
            {
                return true;
            }

            if ((_settings.CloseKey != KeyCode.None && keyCode == _settings.CloseKey) || keyCode == KeyCode.Escape ||
                character == 27)
            {
                CloseRequested?.Invoke();
                return true;
            }

            if (keyCode == KeyCode.Return || keyCode == KeyCode.KeypadEnter || character == '\n' || character == '\r')
            {
                if (frame == _lastSubmitFrame)
                {
                    return true;
                }

                _lastSubmitFrame = frame;
                string text = _input.value ?? "";
                Submitted?.Invoke(text);
                if (_inputOpen)
                {
                    _input.SetValueWithoutNotify("");
                }

                return true;
            }

            return false;
        }

        /// <summary>Re-focuses the input when it lost focus to nothing, or closes it when another field took focus.</summary>
        internal void HandleInputFocusLost(bool focusMovedElsewhere)
        {
            if (!_inputOpen)
            {
                return;
            }

            if (focusMovedElsewhere)
            {
                CloseRequested?.Invoke();
                return;
            }

            if (_input.panel != null)
            {
                _input.schedule.Execute(FocusInputAtEnd);
            }
        }

        private static readonly AgentStatusKind[] StatusKinds =
        {
            AgentStatusKind.Idle,
            AgentStatusKind.Thinking,
            AgentStatusKind.Answering,
            AgentStatusKind.RunningTool,
            AgentStatusKind.Retrying
        };

        private void BindCallbacks()
        {
            if (_callbacksBound)
            {
                return;
            }

            _callbacksBound = true;
            _input.RegisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
            _input.RegisterCallback<FocusOutEvent>(OnInputFocusOut);
            _feed.RegisterCallback<WheelEvent>(OnFeedWheel);
            _feed.contentContainer.RegisterCallback<GeometryChangedEvent>(OnFeedContentGeometryChanged);
        }

        private void UnbindCallbacks()
        {
            if (!_callbacksBound)
            {
                return;
            }

            _callbacksBound = false;
            _input.UnregisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
            _input.UnregisterCallback<FocusOutEvent>(OnInputFocusOut);
            _feed.UnregisterCallback<WheelEvent>(OnFeedWheel);
            _feed.contentContainer.UnregisterCallback<GeometryChangedEvent>(OnFeedContentGeometryChanged);
        }

        private void OnInputKeyDown(KeyDownEvent evt)
        {
            if (HandleInputKey(evt.keyCode, evt.character, Time.frameCount))
            {
                evt.StopImmediatePropagation();
                VisualElement target = evt.target as VisualElement ?? _input;
                target.focusController?.IgnoreEvent(evt);
            }
        }

        private void OnInputFocusOut(FocusOutEvent evt)
        {
            HandleInputFocusLost(evt.relatedTarget != null);
        }

        private void OnFeedWheel(WheelEvent evt)
        {
            if (!_inputOpen || _feed.panel == null)
            {
                return;
            }

            _feed.schedule.Execute(UpdateStickToBottom);
        }

        private void OnFeedContentGeometryChanged(GeometryChangedEvent evt)
        {
            if (_stickToBottom)
            {
                ScheduleScrollToBottom();
            }
        }

        private void UpdateStickToBottom()
        {
            Scroller scroller = _feed.verticalScroller;
            _stickToBottom = scroller == null || scroller.value >= scroller.highValue - 1f;
        }

        private void ScheduleScrollToBottom()
        {
            if (!_stickToBottom || _scrollScheduled || _feed.panel == null)
            {
                return;
            }

            _scrollScheduled = true;
            _feed.schedule.Execute(ScrollToBottomNow);
        }

        private void ScrollToBottomNow()
        {
            _scrollScheduled = false;
            Scroller scroller = _feed.verticalScroller;
            if (scroller != null && _stickToBottom)
            {
                scroller.value = scroller.highValue;
            }
        }

        private void FocusInputAtEnd()
        {
            if (!_inputOpen)
            {
                return;
            }

            _input.Focus();
            int length = (_input.value ?? "").Length;
            _input.SelectRange(length, length);
            if (_input.panel != null && _input.focusController?.focusedElement != _input)
            {
                // WHY: a field that was display:none a moment ago cannot take focus until the next layout pass.
                _input.schedule.Execute(() =>
                {
                    if (_inputOpen)
                    {
                        _input.Focus();
                        int end = (_input.value ?? "").Length;
                        _input.SelectRange(end, end);
                    }
                });
            }
        }

        private void SetFeedInteractive(bool interactive)
        {
            PickingMode mode = interactive ? PickingMode.Position : PickingMode.Ignore;
            _feed.pickingMode = mode;
            _feed.contentViewport.pickingMode = mode;
            _feed.contentContainer.pickingMode = mode;
        }

        private bool MatchesSuppressed(KeyCode keyCode, char character)
        {
            if (keyCode != KeyCode.None && keyCode == _suppressKey)
            {
                if (character != '\0')
                {
                    _suppressNextCharacter = false;
                }

                return true;
            }

            if (character == '\0' || _suppressCharacter == '\0')
            {
                return false;
            }

            // WHY: the opening key types what the active keyboard layout maps it to (T is a Cyrillic letter on a
            // Russian layout, the slash key a full stop), so its character cannot be recognised by value: the first
            // character after the opening is the key's own, whichever it is.
            if (_suppressNextCharacter)
            {
                _suppressNextCharacter = false;
                return true;
            }

            char typed = character == '\r' ? '\n' : char.ToLowerInvariant(character);
            return typed == char.ToLowerInvariant(_suppressCharacter);
        }

        private static void Render(ChatFeedLine line, LineElements elements)
        {
            bool hasSpeaker = !string.IsNullOrEmpty(line.Speaker);
            elements.Speaker.text = hasSpeaker ? line.Speaker + ":" : "";
            elements.Speaker.style.display = hasSpeaker ? DisplayStyle.Flex : DisplayStyle.None;
            elements.Text.text = DisplayText(line);
        }

        /// <summary>
        /// The text a feed line shows. Answers and NPC/system lines come from models that write Markdown, and the feed
        /// is plain text, so their markers are stripped (<see cref="ChatMarkdownText.ToPlainText"/>); the player's own
        /// words, command replies and errors are shown verbatim.
        /// </summary>
        internal static string DisplayText(ChatFeedLine line)
        {
            return line.Kind == ChatFeedLineKind.Assistant || line.Kind == ChatFeedLineKind.System
                ? ChatMarkdownText.ToPlainText(line.Text)
                : line.Text;
        }

        private static string KindName(ChatFeedLineKind kind)
        {
            switch (kind)
            {
                case ChatFeedLineKind.Player:
                    return "player";
                case ChatFeedLineKind.Assistant:
                    return "assistant";
                case ChatFeedLineKind.Command:
                    return "command";
                case ChatFeedLineKind.Error:
                    return "error";
                default:
                    return "system";
            }
        }

        private static string KindName(AgentStatusKind kind)
        {
            switch (kind)
            {
                case AgentStatusKind.Thinking:
                    return "thinking";
                case AgentStatusKind.Answering:
                    return "answering";
                case AgentStatusKind.RunningTool:
                    return "tool";
                case AgentStatusKind.Retrying:
                    return "retrying";
                default:
                    return "idle";
            }
        }

        private static Color ColorFor(ChatFeedLineKind kind)
        {
            switch (kind)
            {
                case ChatFeedLineKind.Player:
                    return new Color(1f, 1f, 1f, 1f);
                case ChatFeedLineKind.Assistant:
                    return new Color(0.62f, 0.9f, 1f, 1f);
                case ChatFeedLineKind.Command:
                    return new Color(0.66f, 1f, 0.62f, 1f);
                case ChatFeedLineKind.Error:
                    return new Color(1f, 0.55f, 0.5f, 1f);
                default:
                    return new Color(1f, 0.92f, 0.55f, 1f);
            }
        }

        private void ApplyDefaultStyle()
        {
            float margin = Mathf.Max(0f, _settings.Margin);
            _root.style.position = Position.Absolute;
            _root.style.left = 0f;
            _root.style.right = 0f;
            _root.style.top = 0f;
            _root.style.bottom = 0f;

            _column.style.position = Position.Absolute;
            _column.style.left = margin;
            _column.style.bottom = margin;
            _column.style.width = Mathf.Max(120f, _settings.Width);
            _column.style.maxWidth = Length.Percent(90f);
            _column.style.flexDirection = FlexDirection.Column;
            _column.style.justifyContent = Justify.FlexEnd;

            _feed.style.maxHeight = Mathf.Max(40f, _settings.FeedMaxHeight);
            _feed.style.flexShrink = 1f;
            _feed.style.marginBottom = 4f;

            _input.style.marginLeft = 0f;
            _input.style.marginRight = 0f;
            _input.style.marginTop = 2f;
            _input.style.marginBottom = 2f;
            _input.style.fontSize = _settings.FontSize;
            _input.style.color = Color.white;
            // WHY: the theme's caret is dark and cannot be seen on the dark field, so the player did not see where
            // they were typing.
            _input.textSelection.cursorColor = Color.white;
            _input.textSelection.selectionColor = InputSelection;
            VisualElement inputBox = _input.Q(className: TextField.inputUssClassName);
            if (inputBox != null)
            {
                inputBox.style.backgroundColor = InputBackground;
                inputBox.style.color = Color.white;
                inputBox.style.borderTopColor = InputBorder;
                inputBox.style.borderBottomColor = InputBorder;
                inputBox.style.borderLeftColor = InputBorder;
                inputBox.style.borderRightColor = InputBorder;
                inputBox.style.borderTopWidth = 1f;
                inputBox.style.borderBottomWidth = 1f;
                inputBox.style.borderLeftWidth = 1f;
                inputBox.style.borderRightWidth = 1f;
                inputBox.style.paddingLeft = 6f;
                inputBox.style.paddingRight = 6f;
                inputBox.style.paddingTop = 4f;
                inputBox.style.paddingBottom = 4f;
            }

            _status.style.fontSize = Mathf.Max(8f, _settings.FontSize - 2f);
            _status.style.color = new Color(0.85f, 0.85f, 0.85f, 1f);
            _status.style.backgroundColor = PanelBackground;
            _status.style.paddingLeft = 6f;
            _status.style.paddingRight = 6f;
            _status.style.paddingTop = 2f;
            _status.style.paddingBottom = 2f;
            _status.style.alignSelf = Align.FlexStart;
            _status.style.unityFontStyleAndWeight = FontStyle.Italic;
        }

        private void StyleLine(VisualElement row, Label speaker, Label text, ChatFeedLineKind kind)
        {
            float duration = Mathf.Max(0f, _settings.FadeDurationSeconds);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.FlexStart;
            row.style.backgroundColor = PanelBackground;
            row.style.paddingLeft = 6f;
            row.style.paddingRight = 6f;
            row.style.paddingTop = 2f;
            row.style.paddingBottom = 2f;
            row.style.marginBottom = 1f;
            row.style.transitionProperty = new List<StylePropertyName> { new("opacity") };
            row.style.transitionDuration = new List<TimeValue> { new(duration, TimeUnit.Second) };

            Color color = ColorFor(kind);
            speaker.style.color = color;
            speaker.style.fontSize = _settings.FontSize;
            speaker.style.unityFontStyleAndWeight = FontStyle.Bold;
            speaker.style.marginRight = 6f;
            speaker.style.flexShrink = 0f;
            speaker.style.paddingLeft = 0f;
            speaker.style.paddingRight = 0f;

            text.style.color = kind == ChatFeedLineKind.Player ? Color.white : color;
            text.style.fontSize = _settings.FontSize;
            text.style.whiteSpace = WhiteSpace.Normal;
            text.style.flexShrink = 1f;
            text.style.flexGrow = 1f;
            text.style.paddingLeft = 0f;
            text.style.paddingRight = 0f;
        }

        private sealed class LineElements
        {
            public LineElements(VisualElement row, Label speaker, Label text)
            {
                Row = row;
                Speaker = speaker;
                Text = text;
            }

            public VisualElement Row { get; }

            public Label Speaker { get; }

            public Label Text { get; }
        }
    }
}
