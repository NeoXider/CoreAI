using System.Reflection;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The messenger-style feed: by default the view never moves for assistant content
    /// (<see cref="ChatScrollAnchor.KeepPosition"/>), the jump-to-latest button and unread badge announce what
    /// arrived below the edge, and a reader's own scroll is never undone by a snap still in flight.
    /// </summary>
    [Category("Chat")]
    public sealed class CoreAiChatScrollToLatestEditModeTests
    {
        [Test]
        public void UnreadCounter_CountsOnlyMessagesThatArriveAwayFromTheBottom()
        {
            CoreAiChatUnreadCounter counter = new();

            counter.Observe(true);
            Assert.AreEqual(0, counter.Count, "A message read as it arrives is not unread.");

            counter.Observe(false);
            counter.Observe(false);
            Assert.AreEqual(2, counter.Count);

            counter.MarkRead();
            Assert.AreEqual(0, counter.Count);
        }

        [Test]
        public void UnreadCounter_StopsAtTheBadgeLimit()
        {
            CoreAiChatUnreadCounter counter = new();
            for (int i = 0; i < CoreAiChatUnreadCounter.MaxCount + 20; i++)
            {
                counter.Observe(false);
            }

            Assert.AreEqual(CoreAiChatUnreadCounter.MaxCount, counter.Count);
        }

        [TestCase(true, 0, false, false)]
        [TestCase(true, 3, false, false)]
        [TestCase(false, 0, true, false)]
        [TestCase(false, 2, true, true)]
        public void ButtonAndBadge_FollowReaderPositionAndUnreadCount(
            bool readerAtBottom,
            int unread,
            bool expectButton,
            bool expectBadge)
        {
            Assert.AreEqual(expectButton, CoreAiChatScrollToLatest.ShouldShowButton(readerAtBottom));
            Assert.AreEqual(expectBadge, CoreAiChatScrollToLatest.ShouldShowBadge(readerAtBottom, unread));
        }

        [Test]
        public void FeedScroll_MatchingAValueThePanelSet_IsProgrammatic()
        {
            float[] recent = { float.NaN, 420f, 900f, float.NaN };

            Assert.AreEqual(CoreAiChatPanel.FeedScrollChange.Programmatic,
                CoreAiChatPanel.ClassifyFeedScroll(420.3f, 900f, recent));
        }

        [Test]
        public void FeedScroll_AtTheBottom_IsAtBottom_EvenOnAValueThePanelSet()
        {
            float[] recent = { float.NaN, 420f, 900f, float.NaN };

            Assert.AreEqual(CoreAiChatPanel.FeedScrollChange.AtBottom,
                CoreAiChatPanel.ClassifyFeedScroll(900f, 900f, recent),
                "A reader who returns exactly onto the bottom the panel once set must release the hold.");
        }

        [Test]
        public void FeedScroll_ReaderMove_IsClassifiedByWhereItEnded()
        {
            float[] recent = { 900f, float.NaN, float.NaN, float.NaN };

            Assert.AreEqual(CoreAiChatPanel.FeedScrollChange.ReaderAway,
                CoreAiChatPanel.ClassifyFeedScroll(300f, 900f, recent));
            Assert.AreEqual(CoreAiChatPanel.FeedScrollChange.ReaderAway,
                CoreAiChatPanel.ClassifyFeedScroll(895f, 1000f, recent),
                "895 of 1000 is further than the at-bottom epsilon - the reader is away.");
            Assert.AreEqual(CoreAiChatPanel.FeedScrollChange.AtBottom,
                CoreAiChatPanel.ClassifyFeedScroll(996f, 1000f, recent));
            Assert.AreEqual(CoreAiChatPanel.FeedScrollChange.AtBottom,
                CoreAiChatPanel.ClassifyFeedScroll(0f, 0f, null),
                "A feed without overflow is always at the bottom.");
        }

        [Test]
        public void DefaultScrollAnchor_KeepsPosition()
        {
            GameObject go = new("CoreAiChatPanel_DefaultAnchor_Test");
            try
            {
                AnchorProbePanel panel = go.AddComponent<AnchorProbePanel>();
                Assert.AreEqual(ChatScrollAnchor.KeepPosition, panel.Anchor);
                Assert.IsFalse(CoreAiChatPanel.MovesViewForAssistantContent(panel.Anchor, true),
                    "Messenger default: an answer does not move the feed even for a reader at the bottom.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ReaderScrollingAway_CancelsPendingSnapsAndStopsFollowing()
        {
            GameObject go = new("CoreAiChatPanel_ReaderScrollAway_Test");
            try
            {
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                Scroller scroller = new(0f, 900f, _ => { }, SliderDirection.Vertical);
                SetPrivateField(panel, "_feedScroller", scroller);
                SetPrivateField(panel, "_readerFollowsBottom", true);

                InvokePrivate(panel, "OnFeedScrollValueChanged", 250f);

                Assert.IsFalse(GetPrivateField<bool>(panel, "_readerFollowsBottom"));
                Assert.IsTrue(GetPrivateField<bool>(panel, "_pendingBottomSnapsCancelled"),
                    "The settle tail of an earlier ScrollToBottom must not drag the reader back down.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void PanelOwnSnap_IsNotMistakenForTheReader()
        {
            GameObject go = new("CoreAiChatPanel_ProgrammaticSnap_Test");
            try
            {
                CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
                Scroller scroller = new(0f, 900f, _ => { }, SliderDirection.Vertical);
                SetPrivateField(panel, "_feedScroller", scroller);
                SetPrivateField(panel, "_readerFollowsBottom", false);

                InvokePrivate(panel, "SetFeedScrollValue", scroller, 400f);
                InvokePrivate(panel, "OnFeedScrollValueChanged", 400f);

                Assert.IsFalse(GetPrivateField<bool>(panel, "_pendingBottomSnapsCancelled"));
                Assert.IsFalse(GetPrivateField<bool>(panel, "_readerFollowsBottom"),
                    "A snap the panel made itself says nothing about the reader.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Affordance_ShowsButtonAndCountsUnread_WhileReaderIsAway()
        {
            PanelSettings panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            GameObject host = new("CoreAiChatScrollToLatest_Host_Test");
            try
            {
                host.SetActive(false);
                UIDocument document = host.AddComponent<UIDocument>();
                document.panelSettings = panelSettings;
                host.SetActive(true);

                VisualElement root = document.rootVisualElement;
                ScrollView feed = new();
                Button button = new() { name = CoreAiChatScrollToLatest.ButtonName };
                Label badge = new() { name = CoreAiChatScrollToLatest.BadgeName };
                root.Add(feed);
                root.Add(button);
                root.Add(badge);

                bool atBottom = false;
                CoreAiChatScrollToLatest affordance = new(() => atBottom, () => { });
                affordance.Bind(root, feed);
                Assert.IsTrue(affordance.IsBound);
                Assert.IsTrue(button.ClassListContains(CoreAiChatScrollToLatest.VisibleClassName));
                Assert.IsFalse(badge.ClassListContains(CoreAiChatScrollToLatest.VisibleClassName),
                    "Nothing unread yet - no badge.");

                affordance.ObserveAssistantMessage(false);
                Assert.AreEqual(1, affordance.UnreadCount);
                Assert.AreEqual("1", badge.text);
                Assert.IsTrue(badge.ClassListContains(CoreAiChatScrollToLatest.VisibleClassName));

                atBottom = true;
                affordance.Refresh();
                Assert.AreEqual(0, affordance.UnreadCount, "Reaching the bottom reads everything.");
                Assert.IsFalse(button.ClassListContains(CoreAiChatScrollToLatest.VisibleClassName));
                Assert.AreEqual(PickingMode.Ignore, button.pickingMode, "A hidden button must not eat clicks.");

                affordance.Unbind(false);
                Assert.IsFalse(affordance.IsBound);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(panelSettings);
            }
        }

        [Test]
        public void Affordance_MessageBelowTheEdge_StaysUnreadUntilLayoutConfirmsItIsOnScreen()
        {
            PanelSettings panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            GameObject host = new("CoreAiChatScrollToLatest_KeepPosition_Test");
            try
            {
                host.SetActive(false);
                UIDocument document = host.AddComponent<UIDocument>();
                document.panelSettings = panelSettings;
                host.SetActive(true);

                VisualElement root = document.rootVisualElement;
                ScrollView feed = new();
                Button button = new() { name = CoreAiChatScrollToLatest.ButtonName };
                Label badge = new() { name = CoreAiChatScrollToLatest.BadgeName };
                root.Add(feed);
                root.Add(button);
                root.Add(badge);

                // WHY: the reader stands at the bottom, but KeepPosition will not move the feed - the new row is
                // not seen, and the pre-layout "at the bottom" must not mark it read.
                bool atBottom = true;
                CoreAiChatScrollToLatest affordance = new(() => atBottom, () => { });
                affordance.Bind(root, feed);

                affordance.ObserveAssistantMessage(false);
                Assert.AreEqual(1, affordance.UnreadCount);

                // WHY: after layout the row is below the edge.
                atBottom = false;
                affordance.Refresh();
                Assert.AreEqual("1", badge.text);
                Assert.IsTrue(badge.ClassListContains(CoreAiChatScrollToLatest.VisibleClassName));
                Assert.IsTrue(button.ClassListContains(CoreAiChatScrollToLatest.VisibleClassName));
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(panelSettings);
            }
        }

        [Test]
        public void Affordance_TreeReinsertedWithoutRebind_KeepsWorking()
        {
            PanelSettings panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            GameObject host = new("CoreAiChatScrollToLatest_Reinsert_Test");
            try
            {
                host.SetActive(false);
                UIDocument document = host.AddComponent<UIDocument>();
                document.panelSettings = panelSettings;
                host.SetActive(true);

                VisualElement root = document.rootVisualElement;
                VisualElement chat = new();
                ScrollView feed = new();
                Button button = new() { name = CoreAiChatScrollToLatest.ButtonName };
                Label badge = new() { name = CoreAiChatScrollToLatest.BadgeName };
                chat.Add(feed);
                chat.Add(button);
                chat.Add(badge);
                root.Add(chat);

                int jumps = 0;
                bool atBottom = false;
                CoreAiChatScrollToLatest affordance = new(() => atBottom, () => jumps++);
                affordance.Bind(chat, feed);

                // WHY: a host tab switch takes the chat tree out and puts the same tree back without BindUI.
                chat.RemoveFromHierarchy();
                Assert.IsTrue(affordance.IsDetached);
                affordance.ObserveAssistantMessage(false);
                Assert.AreEqual(1, affordance.UnreadCount, "A message that lands while the tab is hidden still counts.");

                // WHY: attach runs before layout - the stale range says "at the bottom" for the row that landed
                // while the tab was hidden. It must stay unread until a geometry pass confirms it is on screen.
                atBottom = true;
                root.Add(chat);
                Assert.IsFalse(affordance.IsDetached);
                Assert.AreEqual(1, affordance.UnreadCount, "Re-attach before layout must not mark the answer read.");
                atBottom = false;
                affordance.ObserveAssistantMessage(false);
                Assert.AreEqual(2, affordance.UnreadCount);
                Assert.IsTrue(affordance.IsBound, "The same tree came back - the button must still work.");
                Assert.AreEqual("2", badge.text);
                Assert.IsTrue(badge.ClassListContains(CoreAiChatScrollToLatest.VisibleClassName));

                using (ClickEvent click = ClickEvent.GetPooled())
                {
                    click.target = button;
                    button.SendEvent(click);
                }

                Assert.AreEqual(1, jumps, "A click after re-insertion reaches the panel.");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(panelSettings);
            }
        }

        [Test]
        public void Affordance_WithoutButtonInMarkup_StaysUnbound()
        {
            VisualElement root = new();
            ScrollView feed = new();
            root.Add(feed);

            CoreAiChatScrollToLatest affordance = new(() => false, () => { });
            affordance.Bind(root, feed);
            affordance.ObserveAssistantMessage(false);

            Assert.IsFalse(affordance.IsBound);
            Assert.AreEqual(0, affordance.UnreadCount, "Without a button nothing could ever mark it read.");
        }

        private sealed class AnchorProbePanel : CoreAiChatPanel
        {
            public ChatScrollAnchor Anchor => ScrollAnchor;
        }

        private static void SetPrivateField<T>(CoreAiChatPanel panel, string fieldName, T value)
        {
            FieldInfo field = typeof(CoreAiChatPanel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Field {fieldName} not found.");
            field.SetValue(panel, value);
        }

        private static T GetPrivateField<T>(CoreAiChatPanel panel, string fieldName)
        {
            FieldInfo field = typeof(CoreAiChatPanel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Field {fieldName} not found.");
            return (T)field.GetValue(panel);
        }

        private static void InvokePrivate(CoreAiChatPanel panel, string methodName, params object[] args)
        {
            MethodInfo method = typeof(CoreAiChatPanel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"Method {methodName} not found.");
            method.Invoke(panel, args);
        }
    }
}
