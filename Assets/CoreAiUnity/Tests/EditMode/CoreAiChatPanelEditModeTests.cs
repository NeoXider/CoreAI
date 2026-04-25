using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class CoreAiChatPanelEditModeTests
    {
        [Test]
        public void IsEscapeKey_EscapeKeyCode_ReturnsTrue()
        {
            bool isEscape = CoreAiChatPanel.IsEscapeKey(KeyCode.Escape, character: '\0');
            Assert.IsTrue(isEscape);
        }

        [Test]
        public void IsEscapeKey_EscapeCharacter_ReturnsTrue()
        {
            bool isEscape = CoreAiChatPanel.IsEscapeKey(KeyCode.None, (char)27);
            Assert.IsTrue(isEscape);
        }

        [Test]
        public void IsEscapeKey_OtherKey_ReturnsFalse()
        {
            bool isEscape = CoreAiChatPanel.IsEscapeKey(KeyCode.Return, '\n');
            Assert.IsFalse(isEscape);
        }

        [Test]
        public void GetSendButtonPresentation_WhenIdle_ReturnsSendState()
        {
            Assert.AreEqual(">", CoreAiChatPanel.GetSendButtonText(isBusy: false));
            Assert.AreEqual("Отправить сообщение", CoreAiChatPanel.GetSendButtonTooltip(isBusy: false));
        }

        [Test]
        public void GetSendButtonPresentation_WhenBusy_ReturnsStopState()
        {
            Assert.AreEqual("X", CoreAiChatPanel.GetSendButtonText(isBusy: true));
            Assert.AreEqual("Остановить генерацию (Esc)", CoreAiChatPanel.GetSendButtonTooltip(isBusy: true));
        }
    }
}
