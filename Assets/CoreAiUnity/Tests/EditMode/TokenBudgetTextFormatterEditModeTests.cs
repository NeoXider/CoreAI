using CoreAI.Ai;
using CoreAI.Diagnostics;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="TokenBudgetTextFormatter"/>: the shared text layer behind
    /// the IMGUI overlay and <see cref="CoreAiTokenBudgetUiView"/> UGUI bindings.
    /// </summary>
    [TestFixture]
    public sealed class TokenBudgetTextFormatterEditModeTests
    {
        [Test]
        public void FormatTokens_RendersLastSessionAndRequestLines()
        {
            TokenBudgetCalculator calc = new();
            calc.RecordUsage(100, 50, 150, 0d);

            string text = TokenBudgetTextFormatter.FormatTokens(calc);

            StringAssert.Contains("Last request: 100 in / 50 out / 150 total", text);
            StringAssert.Contains("Session: 100 in / 50 out / 150 total", text);
            StringAssert.Contains("Requests: 1 (with usage: 1)", text);
        }

        [Test]
        public void FormatTokens_UnknownCounts_RenderAsDash()
        {
            TokenBudgetCalculator calc = new();
            calc.RecordUsage(null, null, null, 0d);

            string text = TokenBudgetTextFormatter.FormatTokens(calc);

            StringAssert.Contains("Last request: - in / - out / - total", text);
        }

        [Test]
        public void FormatTokens_NullCalculator_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, TokenBudgetTextFormatter.FormatTokens(null));
        }

        [Test]
        public void FormatCost_NoPricing_ReturnsHint()
        {
            TokenBudgetCalculator calc = new();

            string text = TokenBudgetTextFormatter.FormatCost(calc, 0d, 0d);

            StringAssert.Contains("Prices not set", text);
        }

        [Test]
        public void FormatCost_WithPricing_RendersSessionAndLastCost()
        {
            TokenBudgetCalculator calc = new();
            calc.RecordUsage(2000, 1000, 3000, 0d);

            string text = TokenBudgetTextFormatter.FormatCost(calc, 0.5d, 1.5d);

            // 2000 in @ $0.5/1K + 1000 out @ $1.5/1K = $2.5 for both session and last request.
            StringAssert.Contains("Session: $2.5000 | last request: $2.5000", text);
            StringAssert.Contains("(in $0.5000/1K, out $1.5000/1K)", text);
        }

        [Test]
        public void FormatLoad_NoLimiter_ReportsNotAvailable()
        {
            TokenBudgetCalculator calc = new();

            string text = TokenBudgetTextFormatter.FormatLoad(calc, default, 0d, out bool nearLimit);

            Assert.IsFalse(nearLimit);
            StringAssert.Contains("Chat limiter: n/a", text);
            StringAssert.Contains("All LLM usage: 0 req / 0 tok", text);
        }

        [Test]
        public void FormatLoad_SaturatedLimiter_SetsNearLimit()
        {
            TokenBudgetCalculator calc = new();
            RateLimiterMetrics rate = new(
                acceptedInWindow: 5,
                maxRequestsPerWindow: 5,
                windowSeconds: 60,
                totalRejected: 2);

            string text = TokenBudgetTextFormatter.FormatLoad(calc, rate, 0d, out bool nearLimit);

            Assert.IsTrue(nearLimit);
            StringAssert.Contains("Chat limiter: 5/5 per 60s [##########]", text);
            StringAssert.Contains("Rejected total: 2", text);
        }

        [Test]
        public void FormatLoadBar_ClampsAndScales()
        {
            Assert.AreEqual("[..........]", TokenBudgetTextFormatter.FormatLoadBar(0, 10));
            Assert.AreEqual("[#####.....]", TokenBudgetTextFormatter.FormatLoadBar(5, 10));
            Assert.AreEqual("[##########]", TokenBudgetTextFormatter.FormatLoadBar(15, 10));
            Assert.AreEqual("", TokenBudgetTextFormatter.FormatLoadBar(1, 0));
        }
    }
}
