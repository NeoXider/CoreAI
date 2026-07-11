namespace CoreAI.Ai
{
    /// <summary>Computes chat-history token budget from context window and fixed prompt overhead.</summary>
    public interface IContextBudgetPolicy
    {
        /// <summary>Derives buckets; history budget is lower-bounded.</summary>
        ContextBudget Compute(ContextBudgetRequest request, ITokenEstimator estimator);
    }
}
