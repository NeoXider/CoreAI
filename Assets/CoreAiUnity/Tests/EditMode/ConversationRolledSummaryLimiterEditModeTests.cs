using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class ConversationRolledSummaryLimiterEditModeTests
    {
        [Test]
        public void Apply_WhenUnderCap_ReturnsTrimmedInputUnchanged()
        {
            var est = new HeuristicTokenEstimator();
            string text = "hello";
            Assert.AreEqual("hello", ConversationRolledSummaryLimiter.Apply(text, est, 100));
        }

        [Test]
        public void Apply_WhenOverCap_TruncatesWithEllipsis()
        {
            var est = new HeuristicTokenEstimator();
            string text = new string('a', 400);
            string cut = ConversationRolledSummaryLimiter.Apply(text, est, 20);
            Assert.IsTrue(cut.EndsWith("…"));
            Assert.Less(cut.Length, text.Length);
            Assert.LessOrEqual(est.EstimateText(cut), est.EstimateText(text));
        }

        [Test]
        public void Apply_ZeroOrNegativeCap_ReturnsOriginal()
        {
            var est = new HeuristicTokenEstimator();
            string text = new string('b', 500);
            Assert.AreEqual(text, ConversationRolledSummaryLimiter.Apply(text, est, 0));
            Assert.AreEqual(text, ConversationRolledSummaryLimiter.Apply(text, est, -1));
        }
    }
}
