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
    }
}