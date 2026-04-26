using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode-тесты для ScriptableObject <see cref="CoreAiChatConfig"/> —
    /// конфигурации универсальной чат-панели CoreAI.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatConfigEditModeTests
    {
        [Test]
        public void CreateInstance_Defaults_AreSensible()
        {
            CoreAiChatConfig config = ScriptableObject.CreateInstance<CoreAiChatConfig>();

            Assert.AreEqual("PlayerChat", config.RoleId);
            Assert.AreEqual("AI Chat", config.HeaderTitle);
            Assert.IsFalse(string.IsNullOrEmpty(config.WelcomeMessage));
            Assert.IsTrue(config.EnableStreaming, "стриминг по умолчанию включён");
            Assert.AreEqual(string.Empty, config.TypingIndicatorText,
                "префикс пуст → анимация индикатора показывает только точки \"...\"");
            Assert.AreEqual(500, config.ChatWidth);
            Assert.AreEqual(700, config.ChatHeight);
            Assert.IsTrue(config.SendOnShiftEnter);
            Assert.AreEqual(2000, config.MaxMessageLength);
            Assert.IsFalse(string.IsNullOrEmpty(config.ErrorMessagePrefix));
            Assert.IsFalse(string.IsNullOrEmpty(config.TimeoutMessage));
            Assert.IsFalse(string.IsNullOrEmpty(config.NoResponseMessage));
            Assert.IsTrue(config.LoadPersistedChatOnStartup, "по умолчанию подгружаем сохранённую историю в UI");
            Assert.AreEqual(0, config.MaxPersistedMessagesForUi, "0 = без лимита при подгрузке");

            Object.DestroyImmediate(config);
        }
    }
}
