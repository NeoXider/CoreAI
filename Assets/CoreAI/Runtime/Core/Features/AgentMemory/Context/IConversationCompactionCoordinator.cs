namespace CoreAI.Ai
{
    /// <summary>
    /// Decides whether the orchestrator may rebuild the prompt with tighter history after a context-overflow failure.
    /// </summary>
    public interface IConversationCompactionCoordinator
    {
        /// <summary>
        /// True when another bounded retry after shrinking the history budget is allowed.
        /// </summary>
        bool ShouldRetryAfterContextOverflow(
            LlmCompletionResult failure,
            int passesAlreadyApplied,
            int maxPasses)
        {
            return maxPasses > 0 &&
                   passesAlreadyApplied < maxPasses &&
                   failure != null &&
                   failure.ErrorCode == LlmErrorCode.ContextLengthExceeded;
        }

        /// <summary>
        /// True when one automatic retry after shrinking the history budget is allowed.
        /// </summary>
        [System.Obsolete("Use ShouldRetryAfterContextOverflow(failure, passesAlreadyApplied, maxPasses) instead.")]
        bool ShouldRetryOnceAfterContextOverflow(LlmCompletionResult failure, bool compactionAlreadyApplied);
    }
}
