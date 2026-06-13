namespace CoreAI.Ai
{
    /// <summary>
    /// Truncates rolling conversation summary text to a heuristic token budget.
    /// </summary>
    public static class ConversationRolledSummaryLimiter
    {
        /// <summary>
        /// Returns <paramref name="text"/> unchanged when <paramref name="maxTokens"/> is unset or empty text.
        /// Otherwise trims to the longest prefix whose <see cref="ITokenEstimator.EstimateText"/> is at most <paramref name="maxTokens"/>, then appends an ellipsis when trimmed.
        /// </summary>
        public static string Apply(string text, ITokenEstimator estimator, int maxTokens)
        {
            if (string.IsNullOrEmpty(text) || maxTokens <= 0 || estimator == null)
            {
                return text ?? "";
            }

            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return "";
            }

            if (estimator.EstimateText(trimmed) <= maxTokens)
            {
                return trimmed;
            }

            int lo = 0;
            int hi = trimmed.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (estimator.EstimateText(trimmed.Substring(0, mid)) <= maxTokens)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (lo <= 0)
            {
                return "...";
            }

            string prefix = trimmed.Substring(0, lo).TrimEnd();
            return string.IsNullOrEmpty(prefix) ? "..." : prefix + "...";
        }
    }
}