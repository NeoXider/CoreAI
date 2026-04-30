namespace CoreAI.Ai
{
    /// <summary>
    /// Heuristic token estimate for prompt budgeting (portable core has no model tokenizer).
    /// </summary>
    public interface ITokenEstimator
    {
        /// <summary>Estimated tokens for plain text (minimum 0).</summary>
        int EstimateText(string text);
    }
}
