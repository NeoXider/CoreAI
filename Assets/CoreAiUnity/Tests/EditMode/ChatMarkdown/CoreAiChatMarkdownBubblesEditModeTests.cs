using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.ChatMarkdown;
using CoreAI.ChatOverlay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode.ChatMarkdown
{
    /// <summary>
    /// The chat Markdown seam with a fake backend, so it runs whether or not the renderer package is installed:
    /// without a backend the panel keeps its plain labels; with one, answers become Markdown views, a turn split by a
    /// tool round renders each bubble once with its own text, an unterminated fence never reaches the renderer open,
    /// links are inert by default, copying yields the original Markdown and user messages stay plain.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatMarkdownBubblesEditModeTests
    {
        private ICoreAiMarkdownRenderer _registered;
        private readonly List<string> _clipboard = new();

        [SetUp]
        public void SetUp()
        {
            _registered = CoreAiMarkdownRendererRegistry.Current;
            _clipboard.Clear();
            CoreAiMarkdownClipboard.Writer = text => _clipboard.Add(text);
        }

        [TearDown]
        public void TearDown()
        {
            CoreAiMarkdownRendererRegistry.Register(_registered);
            CoreAiMarkdownClipboard.Writer = null;
        }

        [Test]
        public async Task PackageAbsent_PanelKeepsPlainLabels()
        {
            CoreAiMarkdownRendererRegistry.Register(null);
            using PanelContext context = NewPanel();

            Assert.IsNull(context.Panel.BubbleRenderer, "Without a backend the panel must not pick a renderer.");

            context.Panel.ChatService = Service(new LlmStreamChunk { Text = "**bold** answer" },
                new LlmStreamChunk { IsDone = true });
            await context.Panel.SubmitMessageFromExternalAsync("hi",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
            context.Panel.AddMessage("*buffered*", false);

            Assert.AreEqual(0, context.Scroll.contentContainer.Query<CoreAiMarkdownView>().ToList().Count);
            List<Label> labels = context.Scroll.contentContainer.Query<Label>().Class("coreai-ai-message").ToList();
            Assert.AreEqual(2, labels.Count);
            Assert.AreEqual("**bold** answer", labels[0].text);
            Assert.AreNotEqual(DisplayStyle.None, labels[0].style.display.value);
            Assert.AreEqual("*buffered*", labels[1].text);
        }

        [Test]
        public void PackagePresent_DefaultIsMarkdown_UnlessThePanelTypeRendersItsOwnBubbles()
        {
            CoreAiMarkdownRendererRegistry.Register(new FakeMarkdownRenderer());
            using PanelContext context = NewPanel();

            Assert.IsInstanceOf<CoreAiChatMarkdownBubbles>(context.Panel.BubbleRenderer);
            Assert.IsFalse(CoreAiChatMarkdownBubbles.OverridesBubbleFactory(typeof(CoreAiChatPanel)));
            Assert.IsTrue(CoreAiChatMarkdownBubbles.OverridesBubbleFactory(typeof(OwnBubblesPanel)),
                "A host that overrides CreateMessageBubble already renders its bubbles and must not get a second renderer.");
            Assert.IsNull(CoreAiChatMarkdownBubbles.CreateDefaultFor(null));

            context.Panel.BubbleRenderer = null;
            Assert.IsNull(context.Panel.BubbleRenderer, "A host can switch Markdown off for one panel.");
        }

        [Test]
        public async Task SplitTurn_RendersEachBubbleOnce_WithItsOwnSegment()
        {
            FakeMarkdownRenderer backend = new();
            using PanelContext context = NewPanel();
            CoreAiChatMarkdownBubbles bubbles = new(backend, AdvancingClock());
            context.Panel.BubbleRenderer = bubbles;
            context.Panel.ChatService = Service(
                new LlmStreamChunk { Text = "**Before** the" },
                new LlmStreamChunk { Text = " tools." },
                new LlmStreamChunk { BufferedStreamingNoToolBinding = true, BufferedStreamingUseToolProgressHint = true },
                new LlmStreamChunk { Text = "After *the* tools." },
                new LlmStreamChunk { IsDone = true });

            string response = await context.Panel.SubmitMessageFromExternalAsync("go",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

            Assert.AreEqual("**Before** the tools.After *the* tools.", response.Replace("\n", ""));
            List<CoreAiMarkdownView> views = context.Scroll.contentContainer.Query<CoreAiMarkdownView>().ToList();
            Assert.AreEqual(2, views.Count, "A tool round splits the answer into two bubbles.");
            Assert.AreEqual("**Before** the tools.", views[0].Markdown);
            Assert.AreEqual("After *the* tools.", views[1].Markdown);
            foreach (CoreAiMarkdownView view in views)
            {
                Assert.AreEqual(1, view.FinishedRenderCount, "Each bubble gets exactly one finished render.");
                Assert.IsFalse(view.IsStreaming);
                Assert.IsFalse(view.ClassListContains(CoreAiChatPanel.StreamingActiveUssClassName));
            }

            Assert.AreEqual(0, bubbles.StreamingBubbleCount, "Completed bubbles must be let go.");
            Assert.IsFalse(backend.Rendered.Any(markdown => markdown.Contains("Before") && markdown.Contains("After")),
                "No render may receive the concatenated turn.");
            List<Label> labels = context.Scroll.contentContainer.Query<Label>().Class("coreai-ai-message").ToList();
            Assert.AreEqual(2, labels.Count, "The panel's streaming labels stay in the tree as the text buffer.");
            Assert.AreEqual("**Before** the tools.", labels[0].text);
            Assert.IsTrue(labels.All(label => label.style.display.value == DisplayStyle.None));
            Assert.AreSame(labels[0].parent, views[0].parent);
            Assert.Less(labels[0].parent.IndexOf(labels[0]), labels[0].parent.IndexOf(views[0]),
                "The text label stays first, so a plain Q<Label>() on the row still finds the bubble text.");
        }

        [Test]
        public async Task StreamingUnterminatedFence_NeverReachesTheRendererOpen()
        {
            FakeMarkdownRenderer backend = new();
            using PanelContext context = NewPanel();
            context.Panel.BubbleRenderer = new CoreAiChatMarkdownBubbles(backend, AdvancingClock());
            context.Panel.ChatService = Service(new LlmStreamChunk { Text = "Code:\n\n```lua\nprint(1)" },
                new LlmStreamChunk { IsDone = true });

            await context.Panel.SubmitMessageFromExternalAsync("go",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });

            CollectionAssert.Contains(backend.Rendered, "Code:\n\n",
                "The finished paragraph is a stable block rendered on its own.");
            CollectionAssert.Contains(backend.Rendered, "```lua\nprint(1)\n```", "The streaming tail is closed.");
            Assert.AreEqual("Code:\n\n```lua\nprint(1)\n```", backend.Rendered.Last(),
                "The finished render closes the fence as well.");
        }

        [Test]
        public void BufferedAnswer_IsMarkdown_UserMessage_StaysPlain()
        {
            CoreAiMarkdownRendererRegistry.Register(new FakeMarkdownRenderer());
            using PanelContext context = NewPanel();

            context.Panel.AddMessage("**me**", true);
            context.Panel.AddMessage("**answer**", false);

            Label user = context.Scroll.contentContainer.Query<Label>().Class("coreai-user-message").First();
            Assert.AreEqual("**me**", user.text);
            CoreAiMarkdownView view = context.Scroll.contentContainer.Q<CoreAiMarkdownView>();
            Assert.IsNotNull(view);
            Assert.AreEqual("**answer**", view.Markdown);
            Label source = context.Scroll.contentContainer.Q<Label>(className: "coreai-ai-message");
            Assert.AreEqual("**answer**", source.text,
                "A Markdown bubble keeps the hidden text label that host code finds bubbles by.");
            Assert.AreEqual(DisplayStyle.None, source.style.display.value);
            Assert.IsTrue(view.ClassListContains("coreai-chat-message"));
            Assert.IsTrue(view.ClassListContains("coreai-ai-message"));
            Assert.IsTrue(view.ClassListContains(CoreAiChatMarkdownBubbles.BubbleUssClassName));
        }

        [Test]
        public void Links_AreInertByDefault_AndGoToTheHostHandlerWhenSet()
        {
            FakeMarkdownRenderer backend = new();
            CoreAiMarkdownView view = new("[docs](https://example.com)", backend);
            Assert.IsNull(view.LinkHandler);
            Assert.IsFalse(view.ActivateLink("https://example.com"), "Without a handler a link opens nothing.");
            Assert.DoesNotThrow(() => backend.LastLinkCallback("https://example.com"));

            RecordingLinkHandler handler = new();
            view.LinkHandler = handler;
            backend.LastLinkCallback("https://example.com");
            CollectionAssert.AreEqual(new[] { "https://example.com" }, handler.Targets);

            CoreAiChatMarkdownBubbles bubbles = new(backend);
            Assert.IsNull(bubbles.LinkHandler, "Chat links are inert by default too.");
            bubbles.CreateAssistantContent("[answer link](https://example.org)");
            Assert.DoesNotThrow(() => backend.LastLinkCallback("https://example.org"));
            RecordingLinkHandler late = new();
            bubbles.LinkHandler = late;
            backend.LastLinkCallback("https://example.org");
            CollectionAssert.AreEqual(new[] { "https://example.org" }, late.Targets,
                "A handler set later applies to bubbles already shown.");
        }

        [TestCase("https://example.com/a?b=1", true)]
        [TestCase("http://example.com", true)]
        [TestCase("javascript:alert(1)", false)]
        [TestCase("file:///C:/secrets.txt", false)]
        [TestCase("docs/readme.md", false)]
        [TestCase("#heading", false)]
        [TestCase("", false)]
        public void OpenWebLinks_OpensOnlyAbsoluteWebUrls(string target, bool expected)
        {
            Assert.AreEqual(expected, CoreAiMarkdownLinkHandlers.IsWebUrl(target));
        }

        [Test]
        public void Copy_YieldsTheOriginalMarkdown()
        {
            const string source = "# Title\n\n**bold** and `code` <size=50>";
            CoreAiMarkdownView view = new(source, new FakeMarkdownRenderer());

            Assert.IsTrue(view.CopyMarkdownToClipboard());
            CollectionAssert.AreEqual(new[] { source }, _clipboard);
            Assert.IsTrue(CoreAiMarkdownView.IsCopyShortcut(KeyCode.C, true));
            Assert.IsFalse(CoreAiMarkdownView.IsCopyShortcut(KeyCode.C, false));
            Assert.IsFalse(CoreAiMarkdownView.IsCopyShortcut(KeyCode.V, true));
            Assert.IsFalse(new CoreAiMarkdownView().CopyMarkdownToClipboard(), "An empty view copies nothing.");
        }

        [Test]
        public void StreamedBubble_CopiesItsOwnMarkdown()
        {
            CoreAiChatMarkdownBubbles bubbles = new(new FakeMarkdownRenderer(), AdvancingClock());
            VisualElement slot = new();
            Label bubble = new("**part** one");
            slot.Add(bubble);

            bubbles.OnStreamingTextChanged(bubble);
            bubbles.OnStreamingBubbleCompleted(bubble);

            CoreAiMarkdownView view = slot.Q<CoreAiMarkdownView>();
            Assert.IsTrue(view.CopyMarkdownToClipboard());
            CollectionAssert.AreEqual(new[] { "**part** one" }, _clipboard);
        }

        [Test]
        public void NoBackend_ViewShowsThePlainWords()
        {
            CoreAiMarkdownRendererRegistry.Register(null);
            CoreAiMarkdownView view = new("**Hello** `world`");

            Assert.IsFalse(view.IsRenderedAsMarkdown);
            Label plain = view.Q<Label>(className: CoreAiMarkdownView.PlainUssClassName);
            Assert.AreEqual("Hello world", plain.text);
            Assert.IsFalse(plain.enableRichText);
        }

        [Test]
        public void Streaming_CompleteBlocksRenderOnce_OnlyTheTailIsRenderedAgain()
        {
            FakeMarkdownRenderer backend = new();
            CoreAiMarkdownView view = new(null, backend, AdvancingClock());

            view.UpdateStreaming("One.\n\nTwo.\n");
            view.UpdateStreaming("One.\n\nTwo.\n\nThree.\n");
            view.UpdateStreaming("One.\n\nTwo.\n\nThree.\nFour");

            Assert.AreEqual(1, backend.Rendered.Count(markdown => markdown.Contains("One.")),
                "A block that became stable is never rendered again while the answer streams.");
            Assert.AreEqual("Three.\nFour", backend.Rendered.Last(), "Only the open tail is re-rendered.");
            Assert.AreEqual(3, view.Query(className: CoreAiMarkdownView.PartUssClassName).ToList().Count);
        }

        [Test]
        public void Streaming_ThrottlesTheTail_AndFlushRendersTheSkippedUpdate()
        {
            double now = 10d;
            FakeMarkdownRenderer backend = new();
            CoreAiMarkdownView view = new(null, backend, () => now);

            view.UpdateStreaming("a");
            view.UpdateStreaming("ab");
            Assert.AreEqual(1, view.StreamingRenderCount, "An update inside the pause waits.");
            Assert.AreEqual("ab", view.Markdown, "Copy and state already see the newest text.");

            view.FlushStreaming();
            Assert.AreEqual(2, view.StreamingRenderCount);
            Assert.AreEqual("ab", backend.Rendered.Last());

            now += 1d;
            view.UpdateStreaming("abc");
            Assert.AreEqual(3, view.StreamingRenderCount, "After the pause an update renders at once.");
        }

        [Test]
        public void RendererFailure_ShowsPlainText_WithoutThrowing()
        {
            FakeMarkdownRenderer backend = new() { Throw = true };
            CoreAiMarkdownView view = null;

            Assert.DoesNotThrow(() => view = new CoreAiMarkdownView("**x** y", backend));
            Label plain = view.Q<Label>(className: CoreAiMarkdownView.PlainUssClassName);
            Assert.IsNotNull(plain);
            Assert.AreEqual("x y", plain.text);
        }

        [Test]
        public void CompletingABubbleTwice_RendersItOnce()
        {
            CoreAiChatMarkdownBubbles bubbles = new(new FakeMarkdownRenderer(), AdvancingClock());
            VisualElement slot = new();
            Label bubble = new("text");
            slot.Add(bubble);

            bubbles.OnStreamingTextChanged(bubble);
            bubbles.OnStreamingBubbleCompleted(bubble);
            bubbles.OnStreamingBubbleCompleted(bubble);

            Assert.AreEqual(1, slot.Q<CoreAiMarkdownView>().FinishedRenderCount);
            Assert.AreEqual(DisplayStyle.None, bubble.style.display.value);
        }

        [Test]
        public void BubbleWithoutText_NeverGetsAView()
        {
            CoreAiChatMarkdownBubbles bubbles = new(new FakeMarkdownRenderer(), AdvancingClock());
            VisualElement slot = new();
            Label bubble = new("");
            slot.Add(bubble);

            bubbles.OnStreamingBubbleCompleted(bubble);

            Assert.IsNull(slot.Q<CoreAiMarkdownView>());
            Assert.AreNotEqual(DisplayStyle.None, bubble.style.display.value);
        }

        [Test]
        public void Overlay_AnswerAndNpcLinesShowPlainText_PlayerLinesVerbatim()
        {
            ChatFeed feed = new();
            ChatFeedLine answer = feed.Add(ChatFeedLineKind.Assistant, "AI", "**Done**, see `build`.", 0d);
            ChatFeedLine npc = feed.Add(ChatFeedLineKind.System, "Guard", "*waves* Hello", 0d);
            ChatFeedLine player = feed.Add(ChatFeedLineKind.Player, "Me", "**literally**", 0d);

            Assert.AreEqual("Done, see build.", CoreAiChatOverlayView.DisplayText(answer));
            Assert.AreEqual("waves Hello", CoreAiChatOverlayView.DisplayText(npc));
            Assert.AreEqual("**literally**", CoreAiChatOverlayView.DisplayText(player));
        }

        private static Func<double> AdvancingClock()
        {
            double now = 0d;
            return () => now += 1d;
        }

        private static CoreAiChatService Service(params LlmStreamChunk[] chunks)
        {
            return new CoreAiChatService(new ScriptedStreamingOrchestrator(chunks),
                settings: new CoreAISettingsOptions { EnableStreaming = true, LlmRequestTimeoutSeconds = 0f });
        }

        private static PanelContext NewPanel()
        {
            GameObject go = new("CoreAiChatMarkdownBubbles_Test");
            CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
            panel.SetActorIdentityProvider(new LocalActorIdentityProvider("chat-markdown-test"));
            panel.SetRuntimeOptions(new CoreAiChatOptions { RoleId = "SmartChat" });
            // WHY: plain EditMode tests run no MonoBehaviour lifecycle, so the enabled-panel precondition and a
            // panel-less scroll view are modelled directly.
            SetField(panel, "_lifecycleActive", true);
            ScrollView scroll = new();
            SetField(panel, "MessageScroll", scroll);
            SetField(panel, "ChatContainer", scroll);
            return new PanelContext(go, panel, scroll);
        }

        private static void SetField(CoreAiChatPanel panel, string name, object value)
        {
            typeof(CoreAiChatPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(panel, value);
        }

        private readonly struct PanelContext : IDisposable
        {
            public PanelContext(GameObject go, CoreAiChatPanel panel, ScrollView scroll)
            {
                Go = go;
                Panel = panel;
                Scroll = scroll;
            }

            public GameObject Go { get; }

            public CoreAiChatPanel Panel { get; }

            public ScrollView Scroll { get; }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(Go);
            }
        }

        private sealed class OwnBubblesPanel : CoreAiChatPanel
        {
            protected override VisualElement CreateMessageBubble(string text, bool isUser)
            {
                return base.CreateMessageBubble(text, isUser);
            }
        }

        private sealed class RecordingLinkHandler : ICoreAiMarkdownLinkHandler
        {
            public List<string> Targets { get; } = new();

            public void OnLinkActivated(string target)
            {
                Targets.Add(target);
            }
        }

        private sealed class FakeMarkdownRenderer : ICoreAiMarkdownRenderer
        {
            public List<string> Rendered { get; } = new();

            public Action<string> LastLinkCallback { get; private set; }

            public bool Throw { get; set; }

            public StyleSheet StyleSheet => null;

            public VisualElement Render(string markdown, Action<string> linkActivated)
            {
                if (Throw)
                {
                    throw new InvalidOperationException("The fake renderer failed on purpose.");
                }

                Rendered.Add(markdown);
                LastLinkCallback = linkActivated;
                VisualElement root = new();
                root.Add(new Label(markdown));
                return root;
            }
        }

        private sealed class ScriptedStreamingOrchestrator : IAiOrchestrationService
        {
            private readonly Queue<LlmStreamChunk> _chunks;

            public ScriptedStreamingOrchestrator(IEnumerable<LlmStreamChunk> chunks)
            {
                _chunks = new Queue<LlmStreamChunk>(chunks);
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(string.Empty);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                while (_chunks.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return _chunks.Dequeue();
                    await Task.Yield();
                }
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }
    }
}
