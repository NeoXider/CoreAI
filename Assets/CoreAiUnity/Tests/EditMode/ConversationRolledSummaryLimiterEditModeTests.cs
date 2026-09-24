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
        public void Apply_KeptSuffix_NeverStartsInsideASurrogatePair()
        {
            OnePerCharEstimator est = new();
            string text = new string('a', 50) + char.ConvertFromUtf32(0x1F600) + "tail";

            string cut = ConversationRolledSummaryLimiter.Apply(text, est, 5);

            Assert.AreEqual("…tail", cut, "the budget reaches into the emoji, so the whole pair is dropped");
            Assert.IsFalse(char.IsLowSurrogate(cut[1]));
        }

        private sealed class OnePerCharEstimator : ITokenEstimator
        {
            public int EstimateText(string text)
            {
                return text?.Length ?? 0;
            }
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
