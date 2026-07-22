using System.Reflection;
using CoreAI.Chat;
using CoreAI.Hub.UI;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="HubChatPage"/>'s <see cref="CoreAI.Hub.IHubEscapeHandler"/>
    /// implementation. Default (<see cref="HubChatPage.StopGenerationOnEscape"/> off): Escape always
    /// falls through so the Hub collapses immediately and any in-progress generation keeps running in
    /// the background. Opt-in (<c>StopGenerationOnEscape = true</c>): Escape stops an in-flight turn on
    /// its own first, consuming the key-press so the Hub does not collapse.
    /// </summary>
    [TestFixture]
    public sealed class HubChatPageEditModeTests
    {
        [Test]
        public void TryHandleEscape_WithoutBuiltPanel_ReturnsFalse()
        {
            HubChatPage page = new();

            Assert.IsFalse(page.TryHandleEscape());
        }

        [Test]
        public void TryHandleEscape_WhenPanelIdle_ReturnsFalse()
        {
            GameObject go = new("HubChatPage_TryHandleEscape_Idle_Test");
            try
            {
                HubChatPage page = new(stopGenerationOnEscape: true);
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                SetPanel(page, panel);

                Assert.IsFalse(page.TryHandleEscape());
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Default UX (StopGenerationOnEscape = false, matches the default constructor): Escape does
        /// NOT stop a busy turn — it falls straight through to the Hub, which collapses while the
        /// request keeps running in the background.
        /// </summary>
        [Test]
        public void TryHandleEscape_DefaultAndPanelBusy_ReturnsFalseAndLeavesGenerationRunning()
        {
            GameObject go = new("HubChatPage_TryHandleEscape_DefaultBusy_Test");
            try
            {
                HubChatPage page = new();
                Assert.IsFalse(page.StopGenerationOnEscape, "opt-in stop-on-escape must default to off");

                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                SetPanelField(panel, "_isStreaming", true);
                SetPanel(page, panel);

                bool handled = page.TryHandleEscape();

                Assert.IsFalse(handled, "Escape must fall through to the Hub's collapse by default");
                Assert.IsTrue(panel.IsRequestInProgress, "generation must keep running in the background");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Opt-in: with StopGenerationOnEscape on, a busy turn is stopped and Escape is consumed.</summary>
        [Test]
        public void TryHandleEscape_OptInAndPanelBusy_StopsGenerationAndConsumesEscape()
        {
            GameObject go = new("HubChatPage_TryHandleEscape_OptInBusy_Test");
            try
            {
                HubChatPage page = new(stopGenerationOnEscape: true);
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                SetPanelField(panel, "_isStreaming", true);
                SetPanel(page, panel);

                bool handled = page.TryHandleEscape();

                Assert.IsTrue(handled);
                Assert.IsFalse(panel.IsRequestInProgress, "TryHandleEscape must stop the active generation");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Opt-in + Options.EnableStopGeneration off: Escape is still consumed (busy turn), but
        /// the actual stop is skipped, respecting the "no stop while generating" config.</summary>
        [Test]
        public void TryHandleEscape_OptInBusyAndStopGenerationDisabled_ConsumesEscapeWithoutStopping()
        {
            GameObject go = new("HubChatPage_TryHandleEscape_OptInBusyStopDisabled_Test");
            try
            {
                HubChatPage page = new(stopGenerationOnEscape: true);
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                panel.SetRuntimeOptions(new CoreAiChatOptions { EnableStopGeneration = false });
                SetPanelField(panel, "_isStreaming", true);
                SetPanel(page, panel);

                bool handled = page.TryHandleEscape();

                Assert.IsTrue(handled, "escape is consumed while busy even when Stop Generation is disabled");
                Assert.IsTrue(panel.IsRequestInProgress, "stop must not fire when EnableStopGeneration is off");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary><see cref="HubChatPage.StopGenerationOnEscape"/> is settable after construction, so
        /// host code (e.g. a Settings toggle) can flip it at runtime without rebuilding the page.</summary>
        [Test]
        public void StopGenerationOnEscape_SettableAfterConstruction_ChangesTryHandleEscapeBehavior()
        {
            GameObject go = new("HubChatPage_StopGenerationOnEscape_Settable_Test");
            try
            {
                HubChatPage page = new();
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                SetPanelField(panel, "_isStreaming", true);
                SetPanel(page, panel);

                Assert.IsFalse(page.TryHandleEscape(), "still off by default");

                page.StopGenerationOnEscape = true;

                Assert.IsTrue(page.TryHandleEscape(), "now opted in");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static void SetPanel(HubChatPage page, CoreAiChatPanel panel)
        {
            typeof(HubChatPage)
                .GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(page, panel);
        }

        private static void SetPanelField<T>(CoreAiChatPanel panel, string fieldName, T value)
        {
            typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(panel, value);
        }
    }
}
