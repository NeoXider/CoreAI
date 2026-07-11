namespace CoreAI.Chat
{
    /// <summary>
    /// Unity-free runtime chat settings. Unity ScriptableObject assets should wrap this shape
    /// instead of being used as mutable runtime configuration.
    /// </summary>
    public interface ICoreAiChatOptions
    {
        string RoleId { get; }

        /// <summary>
        /// When true, the chat panel shows an agent/role dropdown so the user can switch the responding
        /// agent (e.g. Programmer, SmartChat, AINpc) at runtime. Default false.
        /// </summary>
        bool AllowAgentSwitching { get; }

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
    /// Optional UI copy overrides for the built-in chat panel.
    /// Kept separate from <see cref="ICoreAiChatOptions"/> so existing custom options remain source-compatible.
    /// </summary>
    public interface ICoreAiChatTextOptions
    {
        string SendButtonText { get; }
        string StopButtonText { get; }
        string SendButtonTooltip { get; }
        string StopButtonTooltip { get; }
        string ClearButtonText { get; }
        string ClearButtonTooltip { get; }
        string CollapseButtonText { get; }
        string CollapseButtonTooltip { get; }
        string CollapseButtonWithEscTooltip { get; }
        string OpenChatTooltip { get; }
        string OpenChatWithHotkeyTooltipFormat { get; }
        string FabFallbackText { get; }
    }

    /// <summary>
    /// Mutable runtime chat settings for tests, bootstrap code, and non-asset configuration.
    /// </summary>
    public sealed class CoreAiChatOptions : ICoreAiChatOptions, ICoreAiChatTextOptions
    {
        public const string DefaultRoleId = Ai.BuiltInAgentRoleIds.SmartChat;
        public const string DefaultHeaderTitle = "AI Chat";
        public const string DefaultWelcomeMessage = "Hello! How can I help?";
        public const string DefaultSendButtonText = ">";
        public const string DefaultStopButtonText = "X";

        public const string DefaultSendButtonTooltip =
            "\u041e\u0442\u043f\u0440\u0430\u0432\u0438\u0442\u044c \u0441\u043e\u043e\u0431\u0449\u0435\u043d\u0438\u0435";

        public const string DefaultStopButtonTooltip =
            "\u041e\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u044c \u0433\u0435\u043d\u0435\u0440\u0430\u0446\u0438\u044e (Esc)";

        public const string DefaultClearButtonText = "C";

        public const string DefaultClearButtonTooltip =
            "\u041e\u0447\u0438\u0441\u0442\u0438\u0442\u044c \u043a\u043e\u043d\u0442\u0435\u043a\u0441\u0442";

        public const string DefaultCollapseButtonText = "-";
        public const string DefaultCollapseButtonTooltip = "Collapse chat";
        public const string DefaultCollapseButtonWithEscTooltip = "Collapse chat (Esc)";
        public const string DefaultOpenChatTooltip = "Open chat";
        public const string DefaultOpenChatWithHotkeyTooltipFormat = "Open chat ({hotkey})";
        public const string DefaultFabFallbackText = "...";
        public const string DefaultStreamingToolProgressHint = "Processing...";
        public const string DefaultLongRequestHintFormat = "Response is still being generated... ~{elapsed}s";
        public const string DefaultErrorMessagePrefix = "Error: ";
        public const string DefaultTimeoutMessage = "Timeout.";
        public const string DefaultNoResponseMessage = "Could not get a response. Try again.";

        public string RoleId { get; set; } = DefaultRoleId;
        public bool AllowAgentSwitching { get; set; }
        public string HeaderTitle { get; set; } = DefaultHeaderTitle;
        public string WelcomeMessage { get; set; } = DefaultWelcomeMessage;
        public string SendButtonText { get; set; } = DefaultSendButtonText;
        public string StopButtonText { get; set; } = DefaultStopButtonText;
        public string SendButtonTooltip { get; set; } = DefaultSendButtonTooltip;
        public string StopButtonTooltip { get; set; } = DefaultStopButtonTooltip;
        public string ClearButtonText { get; set; } = DefaultClearButtonText;
        public string ClearButtonTooltip { get; set; } = DefaultClearButtonTooltip;
        public string CollapseButtonText { get; set; } = DefaultCollapseButtonText;
        public string CollapseButtonTooltip { get; set; } = DefaultCollapseButtonTooltip;
        public string CollapseButtonWithEscTooltip { get; set; } = DefaultCollapseButtonWithEscTooltip;
        public string OpenChatTooltip { get; set; } = DefaultOpenChatTooltip;
        public string OpenChatWithHotkeyTooltipFormat { get; set; } = DefaultOpenChatWithHotkeyTooltipFormat;
        public string FabFallbackText { get; set; } = DefaultFabFallbackText;
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

            ICoreAiChatTextOptions text = source as ICoreAiChatTextOptions;

            return new CoreAiChatOptions
            {
                RoleId = source.RoleId,
                AllowAgentSwitching = source.AllowAgentSwitching,
                HeaderTitle = source.HeaderTitle,
                WelcomeMessage = source.WelcomeMessage,
                SendButtonText = text?.SendButtonText ?? DefaultSendButtonText,
                StopButtonText = text?.StopButtonText ?? DefaultStopButtonText,
                SendButtonTooltip = text?.SendButtonTooltip ?? DefaultSendButtonTooltip,
                StopButtonTooltip = text?.StopButtonTooltip ?? DefaultStopButtonTooltip,
                ClearButtonText = text?.ClearButtonText ?? DefaultClearButtonText,
                ClearButtonTooltip = text?.ClearButtonTooltip ?? DefaultClearButtonTooltip,
                CollapseButtonText = text?.CollapseButtonText ?? DefaultCollapseButtonText,
                CollapseButtonTooltip = text?.CollapseButtonTooltip ?? DefaultCollapseButtonTooltip,
                CollapseButtonWithEscTooltip =
                    text?.CollapseButtonWithEscTooltip ?? DefaultCollapseButtonWithEscTooltip,
                OpenChatTooltip = text?.OpenChatTooltip ?? DefaultOpenChatTooltip,
                OpenChatWithHotkeyTooltipFormat =
                    text?.OpenChatWithHotkeyTooltipFormat ?? DefaultOpenChatWithHotkeyTooltipFormat,
                FabFallbackText = text?.FabFallbackText ?? DefaultFabFallbackText,
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
