using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Character-based heuristic: ~4 characters per token for Latin-heavy content (slightly conservative vs len/3).
    /// </summary>
    public sealed class HeuristicTokenEstimator : ITokenEstimator
    {
        /// <inheritdoc />
        public int EstimateText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int len = text.Length;
            return Math.Max(1, (len + 3) / 4);
        }
    }
}
