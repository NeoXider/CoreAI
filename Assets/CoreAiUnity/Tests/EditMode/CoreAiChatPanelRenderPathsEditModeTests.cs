using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression pins for the chat panel paths that render or re-render transcript content:
    /// the WebGL render cap must sit on the single bubble-append entry point (so the streaming and
    /// persisted-history paths cannot bypass it), the typing animation must not accumulate scheduled
    /// jobs, deferred scroll jobs must survive the panel being torn down under them, and disabling the
    /// panel must leave the embedded tree re-bindable.
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

            Dictionary<string, List<(string Text, bool IsUser)>> cache =
                GetPrivateField<Dictionary<string, List<(string Text, bool IsUser)>>>(
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
                "Streaming calls ShowTypingIndicator per chunk: leaking the previous repeating job floods " +
                "the scheduler and makes the tool-progress hint flicker.");
        }

        [Test]
        public void ShowTypingIndicator_WithoutAuthoredDots_CreatesThreeDotElements()
        {
            using PanelCtx ctx = NewPanelWithAttachedScroll();
            VisualElement indicator = new();
            Label typingLabel = new();
            indicator.Add(typingLabel);
            ctx.Document.rootVisualElement.Add(indicator);
            SetPrivateField(ctx.Panel, "TypingIndicator", indicator);
            SetPrivateField(ctx.Panel, "TypingLabel", typingLabel);

            ctx.Panel.ShowTypingIndicator();

            VisualElement dots = indicator.Q<VisualElement>("coreai-typing-dots");
            Assert.IsNotNull(dots, "A host UXML that predates the dots must still get them built in code.");
            Assert.AreEqual(3, dots.childCount, "The indicator animates three separate dot elements.");
            Assert.IsTrue(dots[2].ClassListContains("coreai-typing-dot--3"),
                "Per-dot classes carry the USS transition-delay stagger.");
        }

        [Test]
        public void HideTypingIndicator_ClearsThePulseClass()
        {
            using PanelCtx ctx = NewPanelWithAttachedScroll();
            VisualElement indicator = new();
            Label typingLabel = new();
            indicator.Add(typingLabel);
            ctx.Document.rootVisualElement.Add(indicator);
            SetPrivateField(ctx.Panel, "TypingIndicator", indicator);
            SetPrivateField(ctx.Panel, "TypingLabel", typingLabel);

            ctx.Panel.ShowTypingIndicator();
            VisualElement dots = indicator.Q<VisualElement>("coreai-typing-dots");
            dots.AddToClassList("coreai-typing-dots--pulse");

            ctx.Panel.HideTypingIndicator();

            Assert.IsFalse(dots.ClassListContains("coreai-typing-dots--pulse"),
                "A hidden indicator must not come back mid-transition on a stale pulse class.");
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

        [Test]
        public void ScheduledScrollJob_AfterUiReferencesCleared_IsSkippedInsteadOfDereferencingNull()
        {
            using PanelCtx ctx = NewPanelWithAttachedScroll();
            int runs = 0;
            System.Action job = () => runs++;

            InvokePrivate(ctx.Panel, "RunMessageScrollJob", job);
            Assert.AreEqual(1, runs, "A live panel must still run the scroll work it scheduled.");

            // ResetUiReferences() is what teardown and every UI rebuild run. It does NOT kill the jobs
            // already queued on the ScrollView: they keep firing on the still-live visual tree until
            // Unity detaches it, and one landing in that window used to dereference the nulled field.
            InvokePrivate(ctx.Panel, "ResetUiReferences");

            Assert.DoesNotThrow(
                () => InvokePrivate(ctx.Panel, "RunMessageScrollJob", job),
                "A scroll job firing after the UI references were cleared must bail out, not throw " +
                "NullReferenceException out of BaseRuntimePanel.Update.");
            Assert.AreEqual(1, runs, "The job body must not run once MessageScroll is gone.");
        }

        [Test]
        public void TypingDotsPulseInterval_CoversTheStyleSheetWave()
        {
            string uss = ReadTypingDotsStyleSheetText();
            int durationMs = MaxTimeMilliseconds(uss, "transition-duration");
            int lastDelayMs = MaxTimeMilliseconds(uss, "transition-delay");

            Assert.Greater(durationMs, 0, "The dot transition must declare a duration in the stylesheet.");
            Assert.GreaterOrEqual(
                PrivateConstInt("TypingDotsPulseIntervalMilliseconds"),
                durationMs + lastDelayMs,
                "The class flip that drives the dots must not reverse a wave that is still travelling. " +
                "The interval lives in C# and the timing in USS, so this pin is the only thing that " +
                "notices when one of the two moves.");
        }

        // ---------- helpers ----------

        /// <summary>Clamped length plus the closing marker the render cap appends.</summary>
        private static int MaxRenderedBubbleLength => CoreAiChatPanel.MaxAssistantRenderChars + 32;

        /// <summary>Raw text of the shipped typing-dots stylesheet, wherever the package is mounted.</summary>
        private static string ReadTypingDotsStyleSheetText()
        {
            string[] guids = AssetDatabase.FindAssets("CoreAiChatTypingDots t:StyleSheet");
            Assert.IsNotEmpty(
                guids,
                "CoreAiChatTypingDots.uss must ship with the package — the panel loads it by resource path " +
                "so hosts with their own chat UXML still get the animation.");

            return File.ReadAllText(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        /// <summary>Largest time value declared for <paramref name="property"/> anywhere in the sheet.</summary>
        private static int MaxTimeMilliseconds(string uss, string property)
        {
            int max = 0;
            foreach (Match declaration in Regex.Matches(uss, Regex.Escape(property) + @"\s*:\s*([^;}]+)"))
            {
                foreach (Match time in Regex.Matches(declaration.Groups[1].Value, @"([0-9]*\.?[0-9]+)\s*(ms|s)\b"))
                {
                    float value = float.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture);
                    int milliseconds = time.Groups[2].Value == "s" ? (int)(value * 1000f) : (int)value;
                    if (milliseconds > max)
                    {
                        max = milliseconds;
                    }
                }
            }

            return max;
        }

        private static int PrivateConstInt(string fieldName)
        {
            FieldInfo field = typeof(CoreAiChatPanel)
                .GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Const '{fieldName}' not found on CoreAiChatPanel.");
            return (int)field.GetRawConstantValue();
        }

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
