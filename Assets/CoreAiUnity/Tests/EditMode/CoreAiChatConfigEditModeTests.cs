using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the <see cref="CoreAiChatConfig"/> ScriptableObject
    /// used by the reusable CoreAI chat panel.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatConfigEditModeTests
    {
        [Test]
        public void CreateInstance_Defaults_AreSensible()
        {
            CoreAiChatConfig config = ScriptableObject.CreateInstance<CoreAiChatConfig>();

            Assert.AreEqual("SmartChat", config.RoleId);
            Assert.AreEqual("AI Chat", config.HeaderTitle);
            Assert.IsFalse(string.IsNullOrEmpty(config.WelcomeMessage));
            Assert.AreEqual(CoreAiChatOptions.DefaultSendButtonText, config.SendButtonText);
            Assert.AreEqual(CoreAiChatOptions.DefaultStopButtonText, config.StopButtonText);
            Assert.AreEqual(CoreAiChatOptions.DefaultSendButtonTooltip, config.SendButtonTooltip);
            Assert.AreEqual(CoreAiChatOptions.DefaultStopButtonTooltip, config.StopButtonTooltip);
            Assert.AreEqual(CoreAiChatOptions.DefaultClearButtonText, config.ClearButtonText);
            Assert.AreEqual(CoreAiChatOptions.DefaultClearButtonTooltip, config.ClearButtonTooltip);
            Assert.AreEqual(CoreAiChatOptions.DefaultCollapseButtonText, config.CollapseButtonText);
            Assert.AreEqual(CoreAiChatOptions.DefaultCollapseButtonTooltip, config.CollapseButtonTooltip);
            Assert.AreEqual(CoreAiChatOptions.DefaultCollapseButtonWithEscTooltip,
                config.CollapseButtonWithEscTooltip);
            Assert.AreEqual(CoreAiChatOptions.DefaultOpenChatTooltip, config.OpenChatTooltip);
            Assert.AreEqual(CoreAiChatOptions.DefaultOpenChatWithHotkeyTooltipFormat,
                config.OpenChatWithHotkeyTooltipFormat);
            Assert.AreEqual(CoreAiChatOptions.DefaultFabFallbackText, config.FabFallbackText);
            Assert.IsTrue(config.EnableStreaming, "streaming is on by default");
            Assert.AreEqual(string.Empty, config.TypingIndicatorText,
                "an empty prefix means the indicator animation shows nothing but the dots \"...\"");
            Assert.AreEqual(650, config.ChatWidth);
            Assert.AreEqual(910, config.ChatHeight);
            Assert.IsFalse(config.UseFullscreenChat, "not fullscreen by default");
            Assert.IsFalse(config.SendOnShiftEnter,
                "by default Enter sends and Shift+Enter inserts a line break");
            Assert.AreEqual(2000, config.MaxMessageLength);
            Assert.IsFalse(string.IsNullOrEmpty(config.ErrorMessagePrefix));
            Assert.IsFalse(string.IsNullOrEmpty(config.TimeoutMessage));
            Assert.IsFalse(string.IsNullOrEmpty(config.NoResponseMessage));
            Assert.IsTrue(config.LoadPersistedChatOnStartup, "by default the saved history is loaded into the UI");
            Assert.IsTrue(config.LongRequestHintFormat.Contains("{elapsed}"),
                "the hint template must contain {elapsed} so the seconds can be substituted");
            Assert.IsFalse(string.IsNullOrWhiteSpace(config.StreamingToolProgressHint),
                "the short hint for a tool call / buffering must not be empty by default");
            Assert.IsFalse(config.ShowToolCallsInChat, "by default tool-call lines are not shown in the chat");
            Assert.IsTrue(config.EnableStopGeneration, "by default the user can stop the generation");
            Assert.IsTrue(config.ShowClearButton, "the clear button is available by default");
            Assert.IsTrue(config.ChatRequiresVisibleCursor,
                "by default the chat reacts to hotkeys only while the cursor is visible");
            Assert.IsTrue(config.EnableCameraTool,
                "by default chat agents are given the camera tool");

            Object.DestroyImmediate(config);
        }

        [Test]
        public void ApplyOptions_TextOverrides_RoundTripThroughToOptions()
        {
            CoreAiChatConfig config = ScriptableObject.CreateInstance<CoreAiChatConfig>();

            config.ApplyOptions(new CoreAiChatOptions
            {
                HeaderTitle = "Teacher",
                WelcomeMessage = "Привет",
                SendButtonText = "Отправить",
                StopButtonText = "Стоп",
                SendButtonTooltip = "Отправить в чат",
                StopButtonTooltip = "Остановить ответ",
                ClearButtonText = "Очистить",
                ClearButtonTooltip = "Очистить чат",
                CollapseButtonText = "Свернуть",
                CollapseButtonTooltip = "Свернуть чат",
                CollapseButtonWithEscTooltip = "Свернуть чат (Esc)",
                OpenChatTooltip = "Открыть чат",
                OpenChatWithHotkeyTooltipFormat = "Открыть чат ({hotkey})",
                FabFallbackText = "Чат"
            });

            CoreAiChatOptions options = config.ToOptions();

            Assert.AreEqual("Teacher", options.HeaderTitle);
            Assert.AreEqual("Привет", options.WelcomeMessage);
            Assert.AreEqual("Отправить", options.SendButtonText);
            Assert.AreEqual("Стоп", options.StopButtonText);
            Assert.AreEqual("Отправить в чат", options.SendButtonTooltip);
            Assert.AreEqual("Остановить ответ", options.StopButtonTooltip);
            Assert.AreEqual("Очистить", options.ClearButtonText);
            Assert.AreEqual("Очистить чат", options.ClearButtonTooltip);
            Assert.AreEqual("Свернуть", options.CollapseButtonText);
            Assert.AreEqual("Свернуть чат", options.CollapseButtonTooltip);
            Assert.AreEqual("Свернуть чат (Esc)", options.CollapseButtonWithEscTooltip);
            Assert.AreEqual("Открыть чат", options.OpenChatTooltip);
            Assert.AreEqual("Открыть чат ({hotkey})", options.OpenChatWithHotkeyTooltipFormat);
            Assert.AreEqual("Чат", options.FabFallbackText);

            Object.DestroyImmediate(config);
        }

        [Test]
        public void From_LegacyOptionsWithoutTextOverrides_UsesDefaultText()
        {
            CoreAiChatOptions options = CoreAiChatOptions.From(new LegacyChatOptions());

            Assert.AreEqual(CoreAiChatOptions.DefaultSendButtonText, options.SendButtonText);
            Assert.AreEqual(CoreAiChatOptions.DefaultStopButtonText, options.StopButtonText);
            Assert.AreEqual(CoreAiChatOptions.DefaultClearButtonText, options.ClearButtonText);
            Assert.AreEqual(CoreAiChatOptions.DefaultOpenChatTooltip, options.OpenChatTooltip);
        }

        private sealed class LegacyChatOptions : ICoreAiChatOptions
        {
            public string RoleId => CoreAiChatOptions.DefaultRoleId;
            public bool AllowAgentSwitching => false;
            public string HeaderTitle => CoreAiChatOptions.DefaultHeaderTitle;
            public string WelcomeMessage => CoreAiChatOptions.DefaultWelcomeMessage;
            public bool LoadPersistedChatOnStartup => true;
            public int MaxPersistedMessagesForUi => 0;
            public bool EnableStreaming => true;
            public bool EnableStopGeneration => true;
            public bool ShowToolCallsInChat => false;
            public bool ShowClearButton => true;
            public string TypingIndicatorText => string.Empty;
            public string StreamingToolProgressHint => CoreAiChatOptions.DefaultStreamingToolProgressHint;
            public string LongRequestHintFormat => CoreAiChatOptions.DefaultLongRequestHintFormat;
            public bool UseFullscreenChat => false;
            public int ChatWidth => 650;
            public int ChatHeight => 910;
            public bool SendOnShiftEnter => false;
            public int MaxMessageLength => 2000;
            public bool EnableOpenChatKeyboardShortcut => true;
            public bool EnableEscapeChatShortcuts => true;
            public bool ChatRequiresVisibleCursor => true;
            public bool EnableCameraTool => true;
            public string ErrorMessagePrefix => CoreAiChatOptions.DefaultErrorMessagePrefix;
            public string TimeoutMessage => CoreAiChatOptions.DefaultTimeoutMessage;
            public string NoResponseMessage => CoreAiChatOptions.DefaultNoResponseMessage;
        }
    }
}
