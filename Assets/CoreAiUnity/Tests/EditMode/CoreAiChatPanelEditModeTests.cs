using CoreAI.Chat;
using NUnit.Framework;
using System.Reflection;
using System.Threading;
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

        /// <summary>
        /// Regression (RedoSchool COREAI_FIXES_REQUEST): stop must be reachable while a streaming turn is active
        /// even before the first visible token — UI treats streaming as a busy state alongside sending.
        /// </summary>
        [Test]
        public void IsChatInputLocked_WhenSendingOrStreaming_ReturnsTrue()
        {
            Assert.IsTrue(CoreAiChatPanel.IsChatInputLocked(true, false, false, false), "sending");
            Assert.IsTrue(CoreAiChatPanel.IsChatInputLocked(false, true, false, false), "streaming");
            Assert.IsTrue(CoreAiChatPanel.IsChatInputLocked(true, true, false, false), "sending+streaming");
        }

        [Test]
        public void ShouldSendButtonBeEnabled_WhenRequestIsRunning_ReturnsTrue()
        {
            Assert.IsTrue(CoreAiChatPanel.ShouldSendButtonBeEnabled(
                isSending: true,
                isStreaming: false,
                isStopping: false,
                isClearing: false));

            Assert.IsTrue(CoreAiChatPanel.ShouldSendButtonBeEnabled(
                isSending: false,
                isStreaming: true,
                isStopping: false,
                isClearing: false));
        }

        [Test]
        public void ShouldSendButtonBeEnabled_WhenStoppingOrClearing_ReturnsFalse()
        {
            Assert.IsFalse(CoreAiChatPanel.ShouldSendButtonBeEnabled(
                isSending: true,
                isStreaming: true,
                isStopping: true,
                isClearing: false));

            Assert.IsFalse(CoreAiChatPanel.ShouldSendButtonBeEnabled(
                isSending: false,
                isStreaming: false,
                isStopping: false,
                isClearing: true));
        }

        [Test]
        public void StopAgent_WhenStreamingRequestActive_CancelsCtsAndUnlocksUiState()
        {
            GameObject go = new("CoreAiChatPanel_StopAgent_Test");
            try
            {
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                CancellationTokenSource activeRequestCts = new();

                SetPrivateField(panel, "_isSending", true);
                SetPrivateField(panel, "_isStreaming", true);
                SetPrivateField(panel, "_activeRequestCts", activeRequestCts);

                panel.StopAgent();

                Assert.IsTrue(activeRequestCts.IsCancellationRequested);
                Assert.IsFalse(GetPrivateField<bool>(panel, "_isSending"));
                Assert.IsFalse(GetPrivateField<bool>(panel, "_isStreaming"));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static void SetPrivateField<T>(CoreAiChatPanel panel, string fieldName, T value)
        {
            typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(panel, value);
        }

        private static T GetPrivateField<T>(CoreAiChatPanel panel, string fieldName)
        {
            return (T)typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(panel);
        }
    }
}
