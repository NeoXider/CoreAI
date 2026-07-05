namespace CoreAI.Ai
{
    /// <summary>
    /// Counts tokens for a given text against a specific model. Implementations may use a real
    /// model tokenizer (byte-level BPE) when encoding data is available, and MUST fall back to a
    /// heuristic estimate when the model is unknown or tokenizer data cannot be loaded.
    /// </summary>
    /// <remarks>
    /// This is additive over <see cref="ITokenEstimator"/>. The estimator answers "roughly how many
    /// tokens" without a model context; the counter answers "exactly how many tokens for this model"
    /// when it can, and delegates to the estimator otherwise.
    /// </remarks>
    public interface ITokenCounter
    {
        /// <summary>
        /// Returns the token count for <paramref name="text"/> under the tokenizer of
        /// <paramref name="modelName"/>. Never throws: on unknown model or missing/unloadable
        /// tokenizer data it returns a heuristic estimate. Minimum 0 for null/empty text.
        /// </summary>
        /// <param name="text">Text to tokenize. Null/empty yields 0.</param>
        /// <param name="modelName">
        /// Provider model id (e.g. "gpt-4o", "gpt-4", "text-embedding-3-small"). May be null/empty,
        /// in which case the counter resolves a default encoding or falls back to the estimator.
        /// </param>
        int CountTokens(string text, string modelName);
    }
}