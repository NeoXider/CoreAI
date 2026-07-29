using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression pins for the chat panel paths that render or re-render transcript content:
    /// the WebGL render cap must sit on the single bubble-append entry point (so the streaming and
    /// persisted-history paths cannot bypass it), the typing animation must not accumulate scheduled
    /// jobs, and disabling the panel must leave the embedded tree re-bindable.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatPanelRenderPathsEditModeTests
    {
        private const string HugeAssistantAnswerMarker = "coreai-huge-answer";

        [Test]
        public void AppendMessageBubble_OversizedAssistantText_IsClampedForRender()
        {
            using PanelCtx ctx = NewPanelWithAttachedScroll();

            InvokePrivate(ctx.Panel, "AppendMessageBubble", HugeAssistantAnswer(), false);

            Assert.LessOrEqual(
                SingleAiLabelText(ctx.Scroll).Length,
                MaxRenderedBubbleLength,
                "Rendering an oversized bubble overflows UI Toolkit's GPU vertex buffer and crashes WebGL.");
        }

        [Test]
        public void RoleTranscriptCacheRestore_OversizedAssistantText_IsClampedForRender()
        {
            using PanelCtx ctx = NewPanelWithAttachedScroll();

            // Mirrors the streaming path: the full response is recorded into the per-role cache untouched.
            InvokePrivate(ctx.Panel, "RecordRoleTranscriptMessage", "SmartChat", HugeAssistantAnswer(), false);

            object restored = InvokePrivate(ctx.Panel, "TryRestoreRoleTranscriptFromCache", "SmartChat");

            Assert.IsTrue((bool)restored, "The cached assistant answer should have been re-rendered.");
            Assert.LessOrEqual(
                SingleAiLabelText(ctx.Scroll).Length,
                MaxRenderedBubbleLength,
                "Restoring a cached streaming answer must go through the same WebGL render cap as AddMessage.");
        }

        [Test]
        public void RoleTranscriptCache_KeepsFullTextForHistory()
        {
            using PanelCtx ctx = NewPanelWithAttachedScroll();
            string full = HugeAssistantAnswer();

            InvokePrivate(ctx.Panel, "RecordRoleTranscriptMessage", "SmartChat", full, false);

            var cache = GetPrivateField<Dictionary<string, List<(string Text, bool IsUser)>>>(
                ctx.Panel, "_roleTranscriptCache");

            Assert.AreEqual(full.Length, cache["SmartChat"][0].Text.Length,
                "The cap is render-only: the cache must keep the untruncated answer.");
        }

        [Test]
        public void ShowTypingIndicator_CalledTwice_StopsThePreviousAnimation()
        {
            using PanelCtx ctx = NewPanelWithAttachedScroll();
            VisualElement indicator = new();
            Label typingLabel = new();
            indicator.Add(typingLabel);
            ctx.Document.rootVisualElement.Add(indicator);
            SetPrivateField(ctx.Panel, "TypingIndicator", indicator);
            SetPrivateField(ctx.Panel, "TypingLabel", typingLabel);

            ctx.Panel.ShowTypingIndicator();
            IVisualElementScheduledItem first =
                GetPrivateField<IVisualElementScheduledItem>(ctx.Panel, "_typingAnimation");
            Assert.IsNotNull(first, "The first call must schedule the dot animation.");

            ctx.Panel.ShowTypingIndicator();
            IVisualElementScheduledItem second =
                GetPrivateField<IVisualElementScheduledItem>(ctx.Panel, "_typingAnimation");

            Assert.AreNotSame(first, second, "A second call schedules a new animation item.");
            Assert.IsFalse(
                first.isActive,
                "Streaming calls ShowTypingIndicator per chunk: leaking the previous Every(400) job floods " +
                "the scheduler and makes the tool-progress hint flicker.");
        }

        [Test]
        public void OnDisable_ClearsEmbeddedTreeBuiltFlag_SoReEnableRebindsTheUi()
        {
            GameObject go = new("CoreAiChatPanel_EmbeddedRebind_Test");
            go.SetActive(false);
            try
            {
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                SetPrivateField(panel, "_embeddedHostMode", true);
                SetPrivateField(panel, "_embeddedTreeBuilt", true);

                InvokePrivate(panel, "OnDisable");

                Assert.IsFalse(
                    GetPrivateField<bool>(panel, "_embeddedTreeBuilt"),
                    "OnDisable nulls every UI reference, so BuildEmbeddedChatTree must run again on re-enable " +
                    "— otherwise the embedded chat renders but reacts to nothing.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ---------- helpers ----------

        /// <summary>Clamped length plus the closing marker the render cap appends.</summary>
        private static int MaxRenderedBubbleLength => CoreAiChatPanel.MaxAssistantRenderChars + 32;

        private static string HugeAssistantAnswer()
        {
            return HugeAssistantAnswerMarker + new string('x', 20_000);
        }

        private static string SingleAiLabelText(ScrollView scroll)
        {
            List<Label> labels = scroll.contentContainer
                .Query<Label>().Class("coreai-ai-message").ToList();

            Assert.AreEqual(1, labels.Count, "Exactly one assistant bubble expected.");
            return labels.Single().text;
        }

        private readonly struct PanelCtx : System.IDisposable
        {
            public readonly GameObject Go;
            public readonly GameObject PanelHost;
            public readonly PanelSettings PanelSettings;
            public readonly UIDocument Document;
            public readonly CoreAiChatPanel Panel;
            public readonly ScrollView Scroll;

            public PanelCtx(
                GameObject go,
                GameObject panelHost,
                PanelSettings panelSettings,
                UIDocument document,
                CoreAiChatPanel panel,
                ScrollView scroll)
            {
                Go = go;
                PanelHost = panelHost;
                PanelSettings = panelSettings;
                Document = document;
                Panel = panel;
                Scroll = scroll;
            }

            public void Dispose()
            {
                Object.DestroyImmediate(Go);
                Object.DestroyImmediate(PanelHost);
                Object.DestroyImmediate(PanelSettings);
            }
        }

        /// <summary>
        /// A real UI Toolkit panel is required: bubble rendering schedules a scroll-to-bottom job and the
        /// typing indicator is skipped entirely while its element is unattached.
        /// </summary>
        private static PanelCtx NewPanelWithAttachedScroll()
        {
            PanelSettings panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            GameObject panelHost = new("CoreAiChatPanel_RenderPaths_PanelHost_Test");
            panelHost.SetActive(false);
            UIDocument document = panelHost.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            panelHost.SetActive(true);

            ScrollView scroll = new();
            document.rootVisualElement.Add(scroll);

            GameObject go = new("CoreAiChatPanel_RenderPaths_Test");
            CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
            panel.SetRuntimeOptions(new CoreAiChatOptions { RoleId = "SmartChat" });
            SetPrivateField(panel, "MessageScroll", scroll);
            SetPrivateField(panel, "ChatContainer", scroll);

            return new PanelCtx(go, panelHost, panelSettings, document, panel, scroll);
        }

        private static void SetPrivateField(CoreAiChatPanel panel, string fieldName, object value)
        {
            FieldInfo field = typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Field '{fieldName}' not found on CoreAiChatPanel.");
            field.SetValue(panel, value);
        }

        private static T GetPrivateField<T>(CoreAiChatPanel panel, string fieldName)
        {
            FieldInfo field = typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Field '{fieldName}' not found on CoreAiChatPanel.");
            return (T)field.GetValue(panel);
        }

        private static object InvokePrivate(CoreAiChatPanel panel, string methodName, params object[] args)
        {
            MethodInfo method = typeof(CoreAiChatPanel)
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"Method '{methodName}' not found on CoreAiChatPanel.");
            return method.Invoke(panel, args);
        }
    }
}
