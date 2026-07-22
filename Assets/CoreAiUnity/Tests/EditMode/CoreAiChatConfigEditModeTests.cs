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
            Assert.IsTrue(config.EnableStreaming, "стриминг по умолчанию включён");
            Assert.AreEqual(string.Empty, config.TypingIndicatorText,
                "префикс пуст → анимация индикатора показывает только точки \"...\"");
            Assert.AreEqual(650, config.ChatWidth);
            Assert.AreEqual(910, config.ChatHeight);
            Assert.IsFalse(config.UseFullscreenChat, "по умолчанию не на весь экран");
            Assert.IsFalse(config.SendOnShiftEnter,
                "по умолчанию Enter отправляет, Shift+Enter вставляет перенос строки");
            Assert.AreEqual(2000, config.MaxMessageLength);
            Assert.IsFalse(string.IsNullOrEmpty(config.ErrorMessagePrefix));
            Assert.IsFalse(string.IsNullOrEmpty(config.TimeoutMessage));
            Assert.IsFalse(string.IsNullOrEmpty(config.NoResponseMessage));
            Assert.IsTrue(config.LoadPersistedChatOnStartup, "по умолчанию подгружаем сохранённую историю в UI");
            Assert.IsTrue(config.LongRequestHintFormat.Contains("{elapsed}"),
                "шаблон подсказки должен содержать {elapsed} для подстановки секунд");
            Assert.IsFalse(string.IsNullOrWhiteSpace(config.StreamingToolProgressHint),
                "короткая подсказка при вызове инструмента / буфере не должна быть пустой по умолчанию");
            Assert.IsFalse(config.ShowToolCallsInChat, "по умолчанию tool-call строки в чате не показываем");
            Assert.IsTrue(config.EnableStopGeneration, "по умолчанию пользователь может остановить генерацию");
            Assert.IsTrue(config.ShowClearButton, "по умолчанию кнопка очистки доступна");
            Assert.IsTrue(config.ChatRequiresVisibleCursor,
                "по умолчанию чат реагирует на хоткеи только при видимом курсоре");
            Assert.IsTrue(config.EnableCameraTool,
                "по умолчанию агентам чата выдаётся камера-инструмент");

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
