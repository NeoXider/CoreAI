using CoreAI.Chat;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pure scroll arithmetic behind <see cref="ChatScrollAnchor.AssistantMessageStart"/>: the host pins the
    /// first assistant row by its top and reveals cards with the minimal scroll. Values are scroller
    /// units (content pixels), highValue = contentHeight - viewportHeight.
    /// </summary>
    [Category("Chat")]
    public sealed class ChatScrollAnchorEditModeTests
    {
        /// <summary>
        /// The reason <see cref="ChatScrollAnchor.KeepPosition"/> exists: a reader who scrolled up to
        /// re-read something must not be dragged down by anything the assistant writes — not a message,
        /// not a tool line, not a revealed card. The other two modes do move the view, each in its way.
        /// </summary>
        [TestCase(ChatScrollAnchor.Bottom, true)]
        [TestCase(ChatScrollAnchor.AssistantMessageStart, true)]
        [TestCase(ChatScrollAnchor.KeepPosition, false)]
        public void AssistantContent_MovesTheView_OnlyOutsideKeepPosition(
            ChatScrollAnchor anchor,
            bool expected)
        {
            Assert.AreEqual(expected, CoreAiChatPanel.MovesViewForAssistantContent(anchor));
        }

        [Test]
        public void RowStart_PinsRowTop_WithinScrollerRange()
        {
            Assert.AreEqual(340f, CoreAiChatPanel.ResolveRowStartScrollValue(340f, 0f, 900f));
            Assert.AreEqual(900f, CoreAiChatPanel.ResolveRowStartScrollValue(1200f, 0f, 900f),
                "A row near the end cannot be pinned above the last page — clamp to highValue.");
            Assert.AreEqual(0f, CoreAiChatPanel.ResolveRowStartScrollValue(-5f, 0f, 900f));
        }

        [Test]
        public void RowStart_NaNLayout_FallsBackToBottom()
        {
            Assert.AreEqual(900f, CoreAiChatPanel.ResolveRowStartScrollValue(float.NaN, 0f, 900f));
        }

        [Test]
        public void Reveal_LeavesViewAlone_WhenRowAlreadyVisible()
        {
            // viewport [100, 500), row [200, 400)
            Assert.AreEqual(100f, CoreAiChatPanel.ResolveRevealScrollValue(200f, 400f, 400f, 100f, 0f, 900f));
        }

        [Test]
        public void Reveal_ScrollsDownJustEnough_WhenRowBelowView()
        {
            // viewport [100, 500), row [450, 650) -> bottom aligned: 650 - 400 = 250
            Assert.AreEqual(250f, CoreAiChatPanel.ResolveRevealScrollValue(450f, 650f, 400f, 100f, 0f, 900f));
        }

        [Test]
        public void Reveal_PinsTop_WhenRowAboveViewOrTallerThanViewport()
        {
            Assert.AreEqual(50f, CoreAiChatPanel.ResolveRevealScrollValue(50f, 150f, 400f, 300f, 0f, 900f));
            Assert.AreEqual(300f, CoreAiChatPanel.ResolveRevealScrollValue(300f, 900f, 400f, 0f, 0f, 900f));
        }

        [Test]
        public void Reveal_ClampsToScrollerRange()
        {
            Assert.AreEqual(900f, CoreAiChatPanel.ResolveRevealScrollValue(1000f, 1100f, 400f, 0f, 0f, 900f));
        }
    }
}
