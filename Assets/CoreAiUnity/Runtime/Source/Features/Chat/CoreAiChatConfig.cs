using UnityEngine;

namespace CoreAI.Chat
{
    /// <summary>
    /// ScriptableObject configuration for the CoreAI chat UI and runtime behavior.
    /// </summary>
    [CreateAssetMenu(fileName = "CoreAiChatConfig", menuName = "CoreAI/Chat Config")]
    public class CoreAiChatConfig : ScriptableObject, ICoreAiChatOptions
    {
        [Header("Agent")] [Tooltip("Agent role id used for prompt routing and chat history.")] [SerializeField]
        private string _roleId = CoreAI.Ai.BuiltInAgentRoleIds.SmartChat;

        [Header("UI - Header")] [SerializeField]
        private string _headerTitle = "AI Chat";

        [Header("UI - Welcome")] [SerializeField]
        private string _welcomeMessage = "How can I help?";

        [Tooltip("Load persisted chat history when the panel starts.")] [SerializeField]
        private bool _loadPersistedChatOnStartup = true;

        [Tooltip("Maximum number of persisted messages restored into the UI. Zero disables restore.")] [SerializeField]
        private int _maxPersistedMessagesForUi = 50;

        [Header("UI - Icons")] [Tooltip("Optional AI avatar icon.")] [SerializeField]
        private Sprite _aiAvatarIcon;

        [Tooltip("Optional user avatar icon.")] [SerializeField]
        private Sprite _userAvatarIcon;

        [Header("UI - Streaming")]
        [Tooltip("Display AI responses while they are generated. When false, the UI waits for the full response.")]
        [SerializeField]
        private bool _enableStreaming = true;

        [Tooltip(
            "Allow the chat send button and Esc key to stop an active AI generation. When false, the send button stays disabled until the response finishes.")]
        [SerializeField]
        private bool _enableStopGeneration = true;

        [Tooltip("Show tool-call progress entries in chat when available.")] [SerializeField]
        private bool _showToolCallsInChat = false;

        [Tooltip(
            "Show the clear button in the chat header. ClearChat remains available from code when this is disabled.")]
        [SerializeField]
        private bool _showClearButton = true;

        [Header("UI - Typing Indicator")] [SerializeField]
        private string _typingIndicatorText = "";

        [Tooltip(
            "Hint shown while a streaming response is waiting for tool progress. Empty value uses the panel default.")]
        [SerializeField]
        private string _streamingToolProgressHint = "Processing...";

        [Tooltip(
            "Format shown when a request runs longer than the configured delay. Use {elapsed} for whole seconds; empty disables the hint.")]
        [SerializeField]
        private string _longRequestHintFormat = "Response is still being generated... ~{elapsed}s";

        [Header("UI - Layout")]
        [CoreAiChatLayoutOption]
        [Tooltip("Stretch the chat panel close to fullscreen instead of using the floating window size.")]
        [SerializeField]
        private bool _useFullscreenChat;

        [Tooltip("Floating chat window width in pixels when fullscreen is disabled.")] [SerializeField]
        private int _chatWidth = 650;

        [Tooltip("Floating chat window height in pixels when fullscreen is disabled.")] [SerializeField]
        private int _chatHeight = 910;

        [Header("Input")]
        [Tooltip(
            "When true, Shift+Enter sends the message. When false, Enter sends and Shift+Enter inserts a newline.")]
        [SerializeField]
        private bool _sendOnShiftEnter = false;

        [Tooltip("Maximum message length. Zero disables the limit.")] [SerializeField]
        private int _maxMessageLength = 2000;

        [Header("Hotkeys")] [Tooltip("Allow opening the collapsed chat from the keyboard.")] [SerializeField]
        private bool _enableOpenChatKeyboardShortcut = true;

        [Tooltip("Hotkey used to open the collapsed chat. Ctrl, Cmd, and Alt are not used.")] [SerializeField]
        private KeyCode _openChatHotkey = KeyCode.C;

        [Tooltip("When the chat is open, Esc stops generation or collapses the panel.")] [SerializeField]
        private bool _enableEscapeChatShortcuts = true;

        [Header("Errors")] [SerializeField] private string _errorMessagePrefix = "Error: ";

        [SerializeField] private string _timeoutMessage = "Timeout.";

        [SerializeField] private string _noResponseMessage = "Could not get a response. Try again.";

        // === Public API ===

        public string RoleId => _roleId;
        public string HeaderTitle => _headerTitle;
        public string WelcomeMessage => _welcomeMessage;
        public bool LoadPersistedChatOnStartup => _loadPersistedChatOnStartup;
        public int MaxPersistedMessagesForUi => _maxPersistedMessagesForUi < 0 ? 0 : _maxPersistedMessagesForUi;
        public Sprite AiAvatarIcon => _aiAvatarIcon;
        public Sprite UserAvatarIcon => _userAvatarIcon;
        public bool EnableStreaming => _enableStreaming;
        public bool EnableStopGeneration => _enableStopGeneration;
        public bool ShowToolCallsInChat => _showToolCallsInChat;
        public bool ShowClearButton => _showClearButton;
        public string TypingIndicatorText => _typingIndicatorText;
        public string StreamingToolProgressHint => _streamingToolProgressHint ?? string.Empty;
        public string LongRequestHintFormat => _longRequestHintFormat ?? string.Empty;
        public bool UseFullscreenChat => _useFullscreenChat;
        public int ChatWidth => _chatWidth;
        public int ChatHeight => _chatHeight;
        public bool SendOnShiftEnter => _sendOnShiftEnter;
        public int MaxMessageLength => _maxMessageLength;
        public bool EnableOpenChatKeyboardShortcut => _enableOpenChatKeyboardShortcut;
        public KeyCode OpenChatHotkey => _openChatHotkey;
        public bool EnableEscapeChatShortcuts => _enableEscapeChatShortcuts;
        public string ErrorMessagePrefix => _errorMessagePrefix;
        public string TimeoutMessage => _timeoutMessage;
        public string NoResponseMessage => _noResponseMessage;

        public CoreAiChatOptions ToOptions()
        {
            return CoreAiChatOptions.From(this);
        }

        public void ApplyOptions(ICoreAiChatOptions options)
        {
            if (options == null)
            {
                return;
            }

            _roleId = options.RoleId;
            _headerTitle = options.HeaderTitle;
            _welcomeMessage = options.WelcomeMessage;
            _loadPersistedChatOnStartup = options.LoadPersistedChatOnStartup;
            _maxPersistedMessagesForUi = options.MaxPersistedMessagesForUi;
            _enableStreaming = options.EnableStreaming;
            _enableStopGeneration = options.EnableStopGeneration;
            _showToolCallsInChat = options.ShowToolCallsInChat;
            _showClearButton = options.ShowClearButton;
            _typingIndicatorText = options.TypingIndicatorText;
            _streamingToolProgressHint = options.StreamingToolProgressHint;
            _longRequestHintFormat = options.LongRequestHintFormat;
            _useFullscreenChat = options.UseFullscreenChat;
            _chatWidth = options.ChatWidth;
            _chatHeight = options.ChatHeight;
            _sendOnShiftEnter = options.SendOnShiftEnter;
            _maxMessageLength = options.MaxMessageLength;
            _enableOpenChatKeyboardShortcut = options.EnableOpenChatKeyboardShortcut;
            _enableEscapeChatShortcuts = options.EnableEscapeChatShortcuts;
            _errorMessagePrefix = options.ErrorMessagePrefix;
            _timeoutMessage = options.TimeoutMessage;
            _noResponseMessage = options.NoResponseMessage;
        }
    }
}