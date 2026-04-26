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
        public void IsOpenChatHotkeyFromKeys_C_ReturnsTrue()
        {
            Assert.IsTrue(CoreAiChatPanel.IsOpenChatHotkeyFromKeys(KeyCode.C, KeyCode.C, '\0', false, false, false));
            Assert.IsTrue(CoreAiChatPanel.IsOpenChatHotkeyFromKeys(KeyCode.C, KeyCode.None, 'c', false, false, false));
            Assert.IsTrue(CoreAiChatPanel.IsOpenChatHotkeyFromKeys(KeyCode.C, KeyCode.None, 'C', false, false, false));
        }

        [Test]
        public void IsOpenChatHotkeyFromKeys_CustomLetter_MatchesKeyOrCharacter()
        {
            Assert.IsTrue(CoreAiChatPanel.IsOpenChatHotkeyFromKeys(KeyCode.T, KeyCode.T, '\0', false, false, false));
            Assert.IsTrue(CoreAiChatPanel.IsOpenChatHotkeyFromKeys(KeyCode.T, KeyCode.None, 't', false, false, false));
        }

        [Test]
        public void IsOpenChatHotkeyFromKeys_None_ReturnsFalse()
        {
            Assert.IsFalse(CoreAiChatPanel.IsOpenChatHotkeyFromKeys(KeyCode.None, KeyCode.C, '\0', false, false, false));
        }

        [Test]
        public void IsOpenChatHotkeyFromKeys_WithCtrl_ReturnsFalse()
        {
            Assert.IsFalse(CoreAiChatPanel.IsOpenChatHotkeyFromKeys(KeyCode.C, KeyCode.C, '\0', ctrlHeld: true, false, false));
        }

        [Test]
        public void IsOpenChatHotkeyFromKeys_OtherKey_ReturnsFalse()
        {
            Assert.IsFalse(CoreAiChatPanel.IsOpenChatHotkeyFromKeys(KeyCode.C, KeyCode.V, '\0', false, false, false));
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

        [Test]
        public void IsChatInputLocked_WhenStoppingOrClearing_ReturnsTrue()
        {
            Assert.IsTrue(CoreAiChatPanel.IsChatInputLocked(
                isSending: false,
                isStreaming: false,
                isStopping: true,
                isClearing: false));

            Assert.IsTrue(CoreAiChatPanel.IsChatInputLocked(
                isSending: false,
                isStreaming: false,
                isStopping: false,
                isClearing: true));
        }

        [Test]
        public void IsChatInputLocked_WhenNoBusyFlags_ReturnsFalse()
        {
            Assert.IsFalse(CoreAiChatPanel.IsChatInputLocked(
                isSending: false,
                isStreaming: false,
                isStopping: false,
                isClearing: false));
        }
    }
}
