using CoreAI.Chat;
using NUnit.Framework;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class CoreAiChatPanelEditModeTests
    {
        [Test]
        public void IsEscapeKey_EscapeKeyCode_ReturnsTrue()
        {
            bool isEscape = CoreAiChatPanel.IsEscapeKey(KeyCode.Escape, '\0');
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
            Assert.IsFalse(
                CoreAiChatPanel.IsOpenChatHotkeyFromKeys(KeyCode.None, KeyCode.C, '\0', false, false, false));
        }

        [Test]
        public void IsOpenChatHotkeyFromKeys_WithCtrl_ReturnsFalse()
        {
            Assert.IsFalse(CoreAiChatPanel.IsOpenChatHotkeyFromKeys(KeyCode.C, KeyCode.C, '\0', true, false, false));
        }

        [Test]
        public void IsOpenChatHotkeyFromKeys_OtherKey_ReturnsFalse()
        {
            Assert.IsFalse(CoreAiChatPanel.IsOpenChatHotkeyFromKeys(KeyCode.C, KeyCode.V, '\0', false, false, false));
        }

        [Test]
        public void GetSendButtonPresentation_WhenIdle_ReturnsSendState()
        {
            Assert.AreEqual(">", CoreAiChatPanel.GetSendButtonText(false));
            Assert.AreEqual("Отправить сообщение", CoreAiChatPanel.GetSendButtonTooltip(false));
        }

        [Test]
        public void GetSendButtonPresentation_WhenBusy_ReturnsStopState()
        {
            Assert.AreEqual("X", CoreAiChatPanel.GetSendButtonText(true));
            Assert.AreEqual("Остановить генерацию (Esc)", CoreAiChatPanel.GetSendButtonTooltip(true));
        }

        [Test]
        public void GetSendButtonPresentation_WhenBusyAndStopDisabled_ReturnsSendState()
        {
            Assert.AreEqual(">", CoreAiChatPanel.GetSendButtonText(true, false));
            Assert.AreEqual("Отправить сообщение", CoreAiChatPanel.GetSendButtonTooltip(true, false));
        }

        [Test]
        public void GetSendButtonPresentation_CustomTexts_UsesOverrides()
        {
            Assert.AreEqual("Send", CoreAiChatPanel.GetSendButtonText(false, true, "Send", "Stop"));
            Assert.AreEqual("Stop", CoreAiChatPanel.GetSendButtonText(true, true, "Send", "Stop"));
            Assert.AreEqual("Send", CoreAiChatPanel.GetSendButtonText(true, false, "Send", "Stop"));
            Assert.AreEqual("Send tooltip",
                CoreAiChatPanel.GetSendButtonTooltip(false, true, "Send tooltip", "Stop tooltip"));
            Assert.AreEqual("Stop tooltip",
                CoreAiChatPanel.GetSendButtonTooltip(true, true, "Send tooltip", "Stop tooltip"));
        }

        [Test]
        public void IsChatInputLocked_WhenStoppingOrClearing_ReturnsTrue()
        {
            Assert.IsTrue(CoreAiChatPanel.IsChatInputLocked(
                false,
                false,
                true,
                false));

            Assert.IsTrue(CoreAiChatPanel.IsChatInputLocked(
                false,
                false,
                false,
                true));
        }

        [Test]
        public void IsChatInputLocked_WhenNoBusyFlags_ReturnsFalse()
        {
            Assert.IsFalse(CoreAiChatPanel.IsChatInputLocked(
                false,
                false,
                false,
                false));
        }

        [Test]
        public void ShouldSubmitOnEnter_DefaultEnterSendsShiftEnterNewline()
        {
            Assert.IsTrue(CoreAiChatPanel.ShouldSubmitOnEnter(false, false));
            Assert.IsFalse(CoreAiChatPanel.ShouldSubmitOnEnter(false, true));
        }

        [Test]
        public void ShouldSubmitOnEnter_LegacyShiftEnterModeStillSupported()
        {
            Assert.IsFalse(CoreAiChatPanel.ShouldSubmitOnEnter(true, false));
            Assert.IsTrue(CoreAiChatPanel.ShouldSubmitOnEnter(true, true));
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
                true,
                false,
                false,
                false));

            Assert.IsTrue(CoreAiChatPanel.ShouldSendButtonBeEnabled(
                false,
                true,
                false,
                false));
        }

        [Test]
        public void ShouldSendButtonBeEnabled_WhenRequestIsRunningAndStopDisabled_ReturnsFalse()
        {
            Assert.IsFalse(CoreAiChatPanel.ShouldSendButtonBeEnabled(
                true,
                false,
                false,
                false,
                false));

            Assert.IsFalse(CoreAiChatPanel.ShouldSendButtonBeEnabled(
                false,
                true,
                false,
                false,
                false));
        }

        [Test]
        public void ShouldSendButtonBeEnabled_WhenStoppingOrClearing_ReturnsFalse()
        {
            Assert.IsFalse(CoreAiChatPanel.ShouldSendButtonBeEnabled(
                true,
                true,
                true,
                false));

            Assert.IsFalse(CoreAiChatPanel.ShouldSendButtonBeEnabled(
                false,
                false,
                false,
                true));
        }

        [Test]
        public void FormatPersistedMessageForUi_UserComposerJson_ReturnsHint()
        {
            string content = "{\"telemetry\":{},\"hint\":\"привет\",\"ai_task_source\":\"Chat\"}";

            string formatted = CoreAiChatPanel.FormatPersistedMessageForUi(content, true);

            Assert.AreEqual("привет", formatted);
        }

        [Test]
        public void FormatPersistedMessageForUi_AssistantJson_RemainsUnchanged()
        {
            string content = "{\"hint\":\"не показывать\"}";

            string formatted = CoreAiChatPanel.FormatPersistedMessageForUi(content, false);

            Assert.AreEqual(content, formatted);
        }

        [Test]
        public void FormatPersistedMessageForUi_UserMalformedJson_ReturnsOriginal()
        {
            string content = "{\"telemetry\":{},\"hint\":}";

            string formatted = CoreAiChatPanel.FormatPersistedMessageForUi(content, true);

            Assert.AreEqual(content, formatted);
        }

        [Test]
        public void NormalizeAssistantDisplayText_LeadingWhitespace_TrimsStartOnly()
        {
            string formatted = CoreAiChatPanel.NormalizeAssistantDisplayText("\n\n  Привет\n  мир");

            Assert.AreEqual("Привет\n  мир", formatted);
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

        [Test]
        public void StopAgent_WhenBusyFlagsAreStale_DoesNotThrowAndUnlocksUiState()
        {
            GameObject go = new("CoreAiChatPanel_StopAgent_Stale_Test");
            try
            {
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                CancellationTokenSource activeRequestCts = new();
                activeRequestCts.Dispose();

                SetPrivateField(panel, "_isSending", true);
                SetPrivateField(panel, "_isStreaming", true);
                SetPrivateField(panel, "_activeRequestCts", activeRequestCts);

                Assert.DoesNotThrow(panel.StopAgent);
                Assert.IsFalse(GetPrivateField<bool>(panel, "_isSending"));
                Assert.IsFalse(GetPrivateField<bool>(panel, "_isStreaming"));
                Assert.IsNull(GetPrivateField<CancellationTokenSource>(panel, "_activeRequestCts"));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void StopButton_WhenRequestBusyBeforeActiveRequestCts_CancelsRootAndUnlocksUiState()
        {
            GameObject go = new("CoreAiChatPanel_StopButton_PreRequestCts_Test");
            try
            {
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                CancellationTokenSource rootCts = new();

                SetPrivateField(panel, "_cts", rootCts);
                SetPrivateField(panel, "_isSending", true);
                SetPrivateField<CancellationTokenSource>(panel, "_activeRequestCts", null);

                Assert.DoesNotThrow(() => InvokePrivate(panel, "TrySendInput", true));

                Assert.IsTrue(rootCts.IsCancellationRequested);
                Assert.IsFalse(GetPrivateField<bool>(panel, "_isSending"));
                Assert.IsFalse(GetPrivateField<bool>(panel, "_isStreaming"));
                Assert.IsNotNull(GetPrivateField<CancellationTokenSource>(panel, "_cts"));
                Assert.AreNotSame(rootCts, GetPrivateField<CancellationTokenSource>(panel, "_cts"));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void LongRequestHint_WhenSendingWithoutStreaming_ShowsAfterDelay()
        {
            GameObject go = new("CoreAiChatPanel_LongRequestHint_NonStreaming_Test");
            try
            {
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                Label hint = new();
                hint.style.display = DisplayStyle.None;

                SetPrivateField(panel, "_longRequestHint", hint);
                SetPrivateField(panel, "_longRequestHintArmedSince", Time.realtimeSinceStartup - 5f);
                SetPrivateField(panel, "_isSending", true);
                SetPrivateField(panel, "_isStreaming", false);

                InvokePrivate(panel, "TickLongRequestHint");

                Assert.AreEqual(DisplayStyle.Flex, hint.style.display.value);
                StringAssert.Contains("Response is still being generated", hint.text);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void LongRequestHint_WhenStreamingActive_ClearsAndStaysHidden()
        {
            GameObject go = new("CoreAiChatPanel_LongRequestHint_Streaming_Test");
            try
            {
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                Label hint = new("old hint");
                hint.style.display = DisplayStyle.Flex;

                SetPrivateField(panel, "_longRequestHint", hint);
                SetPrivateField(panel, "_longRequestHintArmedSince", Time.realtimeSinceStartup - 5f);
                SetPrivateField(panel, "_isSending", true);
                SetPrivateField(panel, "_isStreaming", true);

                InvokePrivate(panel, "TickLongRequestHint");

                Assert.AreEqual(DisplayStyle.None, hint.style.display.value);
                Assert.AreEqual(string.Empty, hint.text);
                Assert.IsTrue(float.IsNaN(GetPrivateField<float>(panel, "_longRequestHintArmedSince")));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void StartStreaming_ClearsLongRequestHintImmediately()
        {
            GameObject go = new("CoreAiChatPanel_StartStreaming_LongRequestHint_Test");
            try
            {
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                Label hint = new("old hint");
                hint.style.display = DisplayStyle.Flex;

                SetPrivateField(panel, "_longRequestHint", hint);
                SetPrivateField(panel, "_longRequestHintArmedSince", Time.realtimeSinceStartup - 5f);
                SetPrivateField(panel, "_isSending", true);

                InvokePrivate(panel, "StartStreaming");

                Assert.AreEqual(DisplayStyle.None, hint.style.display.value);
                Assert.AreEqual(string.Empty, hint.text);
                Assert.IsTrue(float.IsNaN(GetPrivateField<float>(panel, "_longRequestHintArmedSince")));
                Assert.IsTrue(GetPrivateField<bool>(panel, "_isStreaming"));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RuntimeHotkeyOverrides_ControlEffectiveProperties()
        {
            GameObject go = new("CoreAiChatPanel_RuntimeHotkeyOverrides_Test");
            try
            {
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                panel.SetRuntimeOptions(new CoreAiChatOptions
                {
                    RoleId = "Teacher",
                    EnableOpenChatKeyboardShortcut = false,
                    EnableEscapeChatShortcuts = true
                });

                Assert.IsFalse(panel.EffectiveOpenChatKeyboardShortcutEnabled);
                Assert.AreEqual(KeyCode.C, panel.EffectiveOpenChatHotkey);
                Assert.IsTrue(panel.EffectiveEscapeChatShortcutsEnabled);

                panel.SetRuntimeOpenChatKeyboardShortcutEnabled(true);
                panel.SetRuntimeOpenChatHotkey(KeyCode.X);
                panel.SetRuntimeEscapeChatShortcutsEnabled(false);

                Assert.IsTrue(panel.EffectiveOpenChatKeyboardShortcutEnabled);
                Assert.AreEqual(KeyCode.X, panel.EffectiveOpenChatHotkey);
                Assert.IsFalse(panel.EffectiveEscapeChatShortcutsEnabled);

                panel.SetRuntimeOpenChatKeyboardShortcutEnabled(null);
                panel.SetRuntimeOpenChatHotkey(null);
                panel.SetRuntimeEscapeChatShortcutsEnabled(null);

                Assert.IsFalse(panel.EffectiveOpenChatKeyboardShortcutEnabled);
                Assert.AreEqual(KeyCode.C, panel.EffectiveOpenChatHotkey);
                Assert.IsTrue(panel.EffectiveEscapeChatShortcutsEnabled);

                panel.SetRuntimeOpenChatKeyboardShortcutEnabled(false);
                panel.SetRuntimeOpenChatHotkey(KeyCode.F);
                panel.SetRuntimeEscapeChatShortcutsEnabled(false);
                panel.ClearRuntimeHotkeyOverrides();

                Assert.IsFalse(panel.EffectiveOpenChatKeyboardShortcutEnabled);
                Assert.AreEqual(KeyCode.C, panel.EffectiveOpenChatHotkey);
                Assert.IsTrue(panel.EffectiveEscapeChatShortcutsEnabled);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SetCollapsed_UpdatesStateAndNotifiesOverrideHook()
        {
            GameObject go = new("CoreAiChatPanel_SetCollapsed_Test");
            try
            {
                TestableCollapsedPanel panel = go.AddComponent<TestableCollapsedPanel>();

                panel.SetCollapsed(true, false);
                panel.SetCollapsed(false, false);

                Assert.IsFalse(panel.IsCollapsed);
                CollectionAssert.AreEqual(new[] { true, false }, panel.CollapsedChanges);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private sealed class TestableCollapsedPanel : CoreAiChatPanel
        {
            public readonly List<bool> CollapsedChanges = new();

            protected override void OnCollapsedStateChanged(bool collapsed)
            {
                CollapsedChanges.Add(collapsed);
                base.OnCollapsedStateChanged(collapsed);
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

        private static void InvokePrivate(CoreAiChatPanel panel, string methodName)
        {
            typeof(CoreAiChatPanel)
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(panel, null);
        }

        private static void InvokePrivate(CoreAiChatPanel panel, string methodName, params object[] args)
        {
            typeof(CoreAiChatPanel)
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(panel, args);
        }
    }
}
