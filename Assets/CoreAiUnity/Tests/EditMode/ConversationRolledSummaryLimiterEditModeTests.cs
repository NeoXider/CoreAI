using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class ConversationRolledSummaryLimiterEditModeTests
    {
        [Test]
        public void Apply_WhenUnderCap_ReturnsTrimmedInputUnchanged()
        {
            HeuristicTokenEstimator est = new();
            string text = "hello";
            Assert.AreEqual("hello", ConversationRolledSummaryLimiter.Apply(text, est, 100));
        }

        [Test]
        public void Apply_WhenOverCap_EvictsOldestAndKeepsNewestWithEllipsis()
        {
            HeuristicTokenEstimator est = new();
            string text = new string('a', 400) + "NEWEST";
            string cut = ConversationRolledSummaryLimiter.Apply(text, est, 20);
            Assert.IsTrue(cut.StartsWith("…"));
            Assert.IsTrue(cut.EndsWith("NEWEST"));
            Assert.Less(cut.Length, text.Length);
            Assert.LessOrEqual(est.EstimateText(cut), est.EstimateText(text));
        }

        [Test]
        public void Apply_ZeroOrNegativeCap_ReturnsOriginal()
        {
            HeuristicTokenEstimator est = new();
            string text = new('b', 500);
            Assert.AreEqual(text, ConversationRolledSummaryLimiter.Apply(text, est, 0));
            Assert.AreEqual(text, ConversationRolledSummaryLimiter.Apply(text, est, -1));
        }
    }
}
