namespace CoreAI.Ai
{
    /// <summary>
    /// Adapts an <see cref="ITokenCounter"/> to the existing <see cref="ITokenEstimator"/> contract
    /// by binding a fixed model name. This lets the budgeting path (which consumes
    /// <see cref="ITokenEstimator"/>) use real BPE counts for a known model without any signature
    /// change, while still falling back to the heuristic estimator inside the counter when BPE data
    /// is unavailable.
    /// </summary>
    public sealed class TokenCounterEstimatorAdapter : ITokenEstimator
    {
        private readonly ITokenCounter _counter;
        private readonly string _modelName;

        /// <param name="counter">Underlying token counter (must not be null).</param>
        /// <param name="modelName">
        /// Model id used for every estimate. When unknown to the counter, the counter itself falls
        /// back to its heuristic estimator.
        /// </param>
        public TokenCounterEstimatorAdapter(ITokenCounter counter, string modelName)
        {
            _counter = counter ?? throw new System.ArgumentNullException(nameof(counter));
            _modelName = modelName;
        }

        /// <inheritdoc />
        public int EstimateText(string text)
        {
            return _counter.CountTokens(text, _modelName);
        }
    }
}
