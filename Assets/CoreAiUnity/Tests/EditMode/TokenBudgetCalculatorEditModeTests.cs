using CoreAI.Diagnostics;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="TokenBudgetCalculator"/>: session token accounting,
    /// rolling-window aggregation, and cost estimation behind the token-budget overlay.
    /// </summary>
    [TestFixture]
    public sealed class TokenBudgetCalculatorEditModeTests
    {
        [Test]
        public void RecordUsage_AccumulatesSessionTotalsAndLastRequest()
        {
            TokenBudgetCalculator calc = new();

            calc.RecordUsage(100, 50, 150, 0d);
            calc.RecordUsage(200, 100, 300, 1d);

            Assert.AreEqual(2, calc.TotalRequests);
            Assert.AreEqual(2, calc.RequestsWithUsage);
            Assert.AreEqual(300, calc.TotalPromptTokens);
            Assert.AreEqual(150, calc.TotalCompletionTokens);
            Assert.AreEqual(450, calc.TotalTokens);
            Assert.AreEqual(200, calc.LastPromptTokens);
            Assert.AreEqual(100, calc.LastCompletionTokens);
            Assert.AreEqual(300, calc.LastTotalTokens);
            Assert.AreEqual(225d, calc.AverageTokensPerRequest, 0.001d);
        }

        [Test]
        public void RecordUsage_MissingTotal_DerivedFromPromptAndCompletion()
        {
            TokenBudgetCalculator calc = new();

            calc.RecordUsage(80, 20, null, 0d);

            Assert.AreEqual(100, calc.TotalTokens);
            Assert.AreEqual(100, calc.LastTotalTokens);
        }

        [Test]
        public void RecordUsage_OnlyTotalReported_AttributedToPromptForCost()
        {
            TokenBudgetCalculator calc = new();

            calc.RecordUsage(null, null, 120, 0d);

            Assert.AreEqual(1, calc.RequestsWithUsage);
            Assert.AreEqual(120, calc.TotalPromptTokens);
            Assert.AreEqual(0, calc.TotalCompletionTokens);
            Assert.AreEqual(120, calc.TotalTokens);
            Assert.AreEqual(-1, calc.LastPromptTokens, "unsplit prompt should stay unknown for display");
        }

        [Test]
        public void RecordUsage_NoUsageReported_CountsRequestOnly()
        {
            TokenBudgetCalculator calc = new();

            calc.RecordUsage(null, null, null, 0d);

            Assert.AreEqual(1, calc.TotalRequests);
            Assert.AreEqual(0, calc.RequestsWithUsage);
            Assert.AreEqual(0, calc.TotalTokens);
            Assert.AreEqual(-1, calc.LastTotalTokens);
            Assert.AreEqual(0d, calc.AverageTokensPerRequest, 0.001d);
            Assert.AreEqual(1, calc.GetRequestsInWindow(0d), "request still counts toward the load window");
        }

        [Test]
        public void RollingWindow_ExpiresOldEntries()
        {
            TokenBudgetCalculator calc = new(60d);

            calc.RecordUsage(10, 10, 20, 0d);
            calc.RecordUsage(10, 10, 20, 30d);
            calc.RecordUsage(10, 10, 20, 70d);

            Assert.AreEqual(2, calc.GetRequestsInWindow(80d), "entry at t=0 is outside [20..80]");
            Assert.AreEqual(40, calc.GetTokensInWindow(80d));
            Assert.AreEqual(0, calc.GetRequestsInWindow(200d), "all entries are outside [140..200]");
            Assert.AreEqual(3, calc.TotalRequests, "session totals never expire");
        }

        [Test]
        public void ComputeCostUsd_UsesPerThousandTokenPrices()
        {
            // 2000 prompt @ $0.5/1K + 1000 completion @ $1.5/1K = $1 + $1.5
            double cost = TokenBudgetCalculator.ComputeCostUsd(2000, 1000, 0.5d, 1.5d);

            Assert.AreEqual(2.5d, cost, 0.0001d);
        }

        [Test]
        public void ComputeCostUsd_NegativeInputsClampToZero()
        {
            Assert.AreEqual(0d, TokenBudgetCalculator.ComputeCostUsd(-5, -5, 1d, 1d), 0.0001d);
            Assert.AreEqual(0d, TokenBudgetCalculator.ComputeCostUsd(1000, 1000, -1d, -1d), 0.0001d);
        }

        [Test]
        public void EstimateSessionCostUsd_MatchesAccumulatedTokens()
        {
            TokenBudgetCalculator calc = new();
            calc.RecordUsage(1000, 500, 1500, 0d);
            calc.RecordUsage(1000, 500, 1500, 1d);

            double cost = calc.EstimateSessionCostUsd(0.25d, 0.75d);

            // 2000 in @ 0.25 + 1000 out @ 0.75 = 0.5 + 0.75
            Assert.AreEqual(1.25d, cost, 0.0001d);
        }

        [Test]
        public void HasPricing_FalseWhenBothPricesUnset()
        {
            Assert.IsFalse(TokenBudgetCalculator.HasPricing(0d, 0d));
            Assert.IsFalse(TokenBudgetCalculator.HasPricing(-1d, 0d));
            Assert.IsTrue(TokenBudgetCalculator.HasPricing(0.1d, 0d));
            Assert.IsTrue(TokenBudgetCalculator.HasPricing(0d, 0.1d));
        }

        [Test]
        public void Reset_ClearsCountersAndWindow()
        {
            TokenBudgetCalculator calc = new();
            calc.RecordUsage(100, 100, 200, 0d);

            calc.Reset();

            Assert.AreEqual(0, calc.TotalRequests);
            Assert.AreEqual(0, calc.TotalTokens);
            Assert.AreEqual(-1, calc.LastTotalTokens);
            Assert.AreEqual(0, calc.GetRequestsInWindow(0d));
        }
    }
}