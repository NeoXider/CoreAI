namespace CoreAI.Chat
{
    /// <summary>
    /// Unity-free runtime chat settings. Unity ScriptableObject assets should wrap this shape
    /// instead of being used as mutable runtime configuration.
    /// </summary>
    public interface ICoreAiChatOptions
    {
        string RoleId { get; }
        string HeaderTitle { get; }
        string WelcomeMessage { get; }
        bool LoadPersistedChatOnStartup { get; }
        int MaxPersistedMessagesForUi { get; }
        bool EnableStreaming { get; }
        bool EnableStopGeneration { get; }
        bool ShowToolCallsInChat { get; }
        bool ShowClearButton { get; }
        string TypingIndicatorText { get; }
        string StreamingToolProgressHint { get; }
        string LongRequestHintFormat { get; }
        bool UseFullscreenChat { get; }
        int ChatWidth { get; }
        int ChatHeight { get; }
        bool SendOnShiftEnter { get; }
        int MaxMessageLength { get; }
        bool EnableOpenChatKeyboardShortcut { get; }
        bool EnableEscapeChatShortcuts { get; }
        string ErrorMessagePrefix { get; }
        string TimeoutMessage { get; }
        string NoResponseMessage { get; }
    }

    /// <summary>
    /// Mutable runtime chat settings for tests, bootstrap code, and non-asset configuration.
    /// </summary>
    public sealed class CoreAiChatOptions : ICoreAiChatOptions
    {
        public const string DefaultRoleId = "SmartChat";
        public const string DefaultHeaderTitle = "AI Chat";
        public const string DefaultWelcomeMessage = "Hello! How can I help?";
        public const string DefaultStreamingToolProgressHint = "Processing...";
        public const string DefaultLongRequestHintFormat = "Response is still being generated... ~{elapsed}s";
        public const string DefaultErrorMessagePrefix = "Error: ";
        public const string DefaultTimeoutMessage = "Timeout.";
        public const string DefaultNoResponseMessage = "Could not get a response. Try again.";

        public string RoleId { get; set; } = DefaultRoleId;
        public string HeaderTitle { get; set; } = DefaultHeaderTitle;
        public string WelcomeMessage { get; set; } = DefaultWelcomeMessage;
        public bool LoadPersistedChatOnStartup { get; set; } = true;
        public int MaxPersistedMessagesForUi { get; set; }
        public bool EnableStreaming { get; set; } = true;
        public bool EnableStopGeneration { get; set; } = true;
        public bool ShowToolCallsInChat { get; set; }
        public bool ShowClearButton { get; set; } = true;
        public string TypingIndicatorText { get; set; } = "";
        public string StreamingToolProgressHint { get; set; } = DefaultStreamingToolProgressHint;
        public string LongRequestHintFormat { get; set; } = DefaultLongRequestHintFormat;
        public bool UseFullscreenChat { get; set; }
        public int ChatWidth { get; set; } = 650;
        public int ChatHeight { get; set; } = 910;
        public bool SendOnShiftEnter { get; set; }
        public int MaxMessageLength { get; set; } = 2000;
        public bool EnableOpenChatKeyboardShortcut { get; set; } = true;
        public bool EnableEscapeChatShortcuts { get; set; } = true;
        public string ErrorMessagePrefix { get; set; } = DefaultErrorMessagePrefix;
        public string TimeoutMessage { get; set; } = DefaultTimeoutMessage;
        public string NoResponseMessage { get; set; } = DefaultNoResponseMessage;

        public static CoreAiChatOptions CreateDefault()
        {
            return new CoreAiChatOptions();
        }

        public static CoreAiChatOptions From(ICoreAiChatOptions source)
        {
            if (source == null)
            {
                return CreateDefault();
            }

            return new CoreAiChatOptions
            {
                RoleId = source.RoleId,
                HeaderTitle = source.HeaderTitle,
                WelcomeMessage = source.WelcomeMessage,
                LoadPersistedChatOnStartup = source.LoadPersistedChatOnStartup,
                MaxPersistedMessagesForUi = source.MaxPersistedMessagesForUi,
                EnableStreaming = source.EnableStreaming,
                EnableStopGeneration = source.EnableStopGeneration,
                ShowToolCallsInChat = source.ShowToolCallsInChat,
                ShowClearButton = source.ShowClearButton,
                TypingIndicatorText = source.TypingIndicatorText,
                StreamingToolProgressHint = source.StreamingToolProgressHint,
                LongRequestHintFormat = source.LongRequestHintFormat,
                UseFullscreenChat = source.UseFullscreenChat,
                ChatWidth = source.ChatWidth,
                ChatHeight = source.ChatHeight,
                SendOnShiftEnter = source.SendOnShiftEnter,
                MaxMessageLength = source.MaxMessageLength,
                EnableOpenChatKeyboardShortcut = source.EnableOpenChatKeyboardShortcut,
                EnableEscapeChatShortcuts = source.EnableEscapeChatShortcuts,
                ErrorMessagePrefix = source.ErrorMessagePrefix,
                TimeoutMessage = source.TimeoutMessage,
                NoResponseMessage = source.NoResponseMessage
            };
        }
    }
}