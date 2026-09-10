using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class DefaultContextBudgetPolicyEditModeTests
    {
        [Test]
        public void Compute_ContextRetryLevelOne_UsesSeventyFivePercentHistoryBudget()
        {
            DefaultContextBudgetPolicy policy = new();
            HeuristicTokenEstimator estimator = new();
            ContextBudgetRequest levelZeroRequest = new()
            {
                MaxContextTokens = 4096,
                MaxOutputTokens = 512,
                ContextRetryLevel = 0
            };
            ContextBudgetRequest levelOneRequest = new()
            {
                MaxContextTokens = 4096,
                MaxOutputTokens = 512,
                ContextRetryLevel = 1
            };

            ContextBudget levelZero = policy.Compute(levelZeroRequest, estimator);
            ContextBudget levelOne = policy.Compute(levelOneRequest, estimator);

            Assert.That(
                (double)levelOne.HistoryTokenBudget / levelZero.HistoryTokenBudget,
                Is.EqualTo(0.75d).Within(0.01d));
        }

        [Test]
        public void ResolveSummaryTokenBudget_SplitsTheConversationAllowance_AndNeverExceedsIt()
        {
            // WHY: the summary used to travel outside every budget; an explicit cap of 0 ("no cap") made
            // it unbounded. The reserve is a third of the allowance, an explicit cap only lowers it.
            Assert.AreEqual(3000, DefaultContextBudgetPolicy.ResolveSummaryTokenBudget(9000, 0),
                "Zero cap: the summary reserve is bounded by the allowance share, not unlimited.");
            Assert.AreEqual(2048, DefaultContextBudgetPolicy.ResolveSummaryTokenBudget(9000, 2048),
                "A smaller explicit cap wins.");
            Assert.AreEqual(3000, DefaultContextBudgetPolicy.ResolveSummaryTokenBudget(9000, 100_000),
                "A larger explicit cap cannot push the summary past its share of the allowance.");
            Assert.AreEqual(0, DefaultContextBudgetPolicy.ResolveSummaryTokenBudget(0, 0));

            DefaultContextBudgetPolicy policy = new();
            ContextBudget budget = policy.Compute(
                new ContextBudgetRequest { MaxContextTokens = 40192, MaxOutputTokens = 512 },
                new HeuristicTokenEstimator());
            int summary = DefaultContextBudgetPolicy.ResolveSummaryTokenBudget(budget.HistoryTokenBudget, 0);
            Assert.Greater(summary, 0);
            Assert.LessOrEqual(
                summary + (budget.HistoryTokenBudget - summary) + budget.EstimatedFixedPromptTokens +
                budget.ReservedForCompletion + budget.ReservedSlackTokens,
                budget.MaxContextTokens,
                "Summary reserve + recent tail + fixed prompt + completion reserve must fit the window.");
        }
    }
}
