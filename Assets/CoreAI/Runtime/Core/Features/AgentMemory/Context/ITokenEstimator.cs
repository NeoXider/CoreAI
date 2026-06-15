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

    /// <summary>
    /// Token estimator that can adjust future pre-flight estimates from observed provider usage.
    /// </summary>
    public interface ICalibratingTokenEstimator : ITokenEstimator
    {
        /// <summary>
        /// Records one completed prompt observation. Values must be positive provider-visible prompt token counts.
        /// </summary>
        void RecordObservation(int estimatedPromptTokens, int realPromptTokens);

        /// <summary>Current bounded multiplier applied to the script-aware base estimate.</summary>
        double CurrentScale { get; }
    }
}
