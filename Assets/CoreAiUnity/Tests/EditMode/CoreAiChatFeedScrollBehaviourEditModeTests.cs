using System;
using System.Collections;
using System.Text;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The 7.50.0 feed rules on a live, laid-out feed (an editor window panel, not a detached tree): a reader's
    /// own scroll is never undone by a snap in flight, a turn anchor re-applied after the reader scrolled up
    /// does not move the feed, and under <see cref="ChatScrollAnchor.KeepPosition"/> an answer that lands below
    /// the edge is announced as unread while the feed stays put.
    /// </summary>
    [Category("Chat")]
    public sealed class CoreAiChatFeedScrollBehaviourEditModeTests
    {
        private const double LayoutTimeoutSeconds = 5;
        private const double SettleChainSeconds = 0.8;

        private sealed class FeedHostWindow : EditorWindow
        {
        }

        private sealed class FeedProbePanel : CoreAiChatPanel
        {
            public ChatScrollAnchor Anchor = ChatScrollAnchor.KeepPosition;

            protected override ChatScrollAnchor ScrollAnchor => Anchor;

            public void Bind(VisualElement root)
            {
                Root = root;
                BindUI();
            }

            public void RequestScrollToBottom() => ScrollToBottom();

            public void ReapplyTurnAnchor() => ScrollToTurnAnchor();
        }

        private FeedHostWindow _window;
        private GameObject _host;
        private FeedProbePanel _panel;
        private ScrollView _feed;
        private Button _button;
        private Label _badge;

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                Object.DestroyImmediate(_host);
            }

            if (_window != null)
            {
                _window.Close();
            }

            _host = null;
            _window = null;
        }

        private IEnumerator Arrange(ChatScrollAnchor anchor, int filler = 30)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                // WHY: an editor window panel needs a graphics device to lay out; -nographics batch runs skip.
                Assert.Ignore("Needs a graphics device (editor window layout); run without -nographics.");
            }

            _window = ScriptableObject.CreateInstance<FeedHostWindow>();
            _window.position = new Rect(0, 0, 600, 600);
            _window.Show();
            VisualElement root = new() { name = "coreai-chat-root" };
            root.style.height = 400;
            _feed = new ScrollView { name = "coreai-chat-scroll" };
            _feed.style.height = 200;
            _feed.style.flexShrink = 0;
            for (int i = 0; i < filler; i++)
            {
                Label line = new($"line {i}");
                line.style.height = 40;
                line.style.flexShrink = 0;
                _feed.Add(line);
            }

            _button = new Button { name = CoreAiChatScrollToLatest.ButtonName };
            _badge = new Label { name = CoreAiChatScrollToLatest.BadgeName };
            root.Add(_feed);
            root.Add(_button);
            root.Add(_badge);
            _window.rootVisualElement.Add(root);

            _host = new GameObject("CoreAiChatFeedScroll_Test");
            _host.SetActive(false);
            _panel = _host.AddComponent<FeedProbePanel>();
            _panel.Anchor = anchor;
            _panel.Bind(root);

            double deadline = EditorApplication.timeSinceStartup + LayoutTimeoutSeconds;
            while (_feed.verticalScroller.highValue <= 1f && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.Greater(_feed.verticalScroller.highValue, 100f, "Stand: the feed must overflow its viewport.");
        }

        private static IEnumerator Wait(double seconds)
        {
            double until = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < until)
            {
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator ReaderScrollDuringBottomSettle_IsNotUndone()
        {
            yield return Arrange(ChatScrollAnchor.KeepPosition);

            _panel.RequestScrollToBottom();
            float readerPosition = 60f;
            _feed.verticalScroller.value = readerPosition;
            yield return Wait(SettleChainSeconds);

            Assert.AreEqual(readerPosition, _feed.verticalScroller.value, 1f,
                "The 80/200/500 ms tail of ScrollToBottom must not drag the reader back down.");
        }

        [UnityTest]
        public IEnumerator AssistantMessageStart_AnchorReappliedAfterReaderScrolledUp_DoesNotMove()
        {
            yield return Arrange(ChatScrollAnchor.AssistantMessageStart);
            _panel.AddMessage(LongText(12), false);
            yield return Wait(SettleChainSeconds);

            float readerPosition = 40f;
            _feed.verticalScroller.value = readerPosition;
            yield return null;
            _panel.ReapplyTurnAnchor();
            yield return Wait(SettleChainSeconds);

            Assert.AreEqual(readerPosition, _feed.verticalScroller.value, 1f,
                "A markdown swap or card rebuild after the reader scrolled up must leave the view alone.");
        }

        [UnityTest]
        public IEnumerator KeepPosition_AnswerBelowTheEdge_StaysPut_AndCountsAsUnread()
        {
            yield return Arrange(ChatScrollAnchor.KeepPosition);
            _feed.verticalScroller.value = _feed.verticalScroller.highValue;
            yield return Wait(0.2);
            float bottomBefore = _feed.verticalScroller.value;

            _panel.AddMessage(LongText(10), false);
            yield return Wait(SettleChainSeconds);

            Assert.AreEqual(bottomBefore, _feed.verticalScroller.value, 1f, "KeepPosition: the feed does not move.");
            Assert.AreEqual(1, _panel.UnreadMessageCount, "The answer landed below the edge: one unread message.");
            Assert.IsTrue(_button.ClassListContains(CoreAiChatScrollToLatest.VisibleClassName));
            Assert.IsTrue(_badge.ClassListContains(CoreAiChatScrollToLatest.VisibleClassName));
        }

        [UnityTest]
        public IEnumerator FollowIfAtBottom_ReaderReturnsExactlyToTheBottom_FollowsAgain()
        {
            yield return Arrange(ChatScrollAnchor.FollowIfAtBottom);
            _panel.RequestScrollToBottom();
            yield return Wait(SettleChainSeconds);
            float bottom = _feed.verticalScroller.highValue;

            _feed.verticalScroller.value = 60f;
            yield return null;
            _feed.verticalScroller.value = bottom;
            yield return null;

            _panel.AddMessage(LongText(10), false);
            yield return Wait(SettleChainSeconds);

            Assert.Greater(_feed.verticalScroller.value, bottom + 1f,
                "The reader came back onto the very bottom the panel once set: the hold is released, the feed follows.");
        }

        private static string LongText(int lines)
        {
            StringBuilder builder = new();
            for (int i = 0; i < lines; i++)
            {
                builder.AppendLine($"Assistant line {i} with enough words to take a whole row of the feed.");
            }

            return builder.ToString();
        }
    }
}
