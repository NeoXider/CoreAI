namespace CoreAI.Ai
{
    /// <inheritdoc />
    public sealed class DefaultConversationCompactionCoordinator : IConversationCompactionCoordinator
    {
        /// <inheritdoc />
        public bool ShouldRetryAfterContextOverflow(
            LlmCompletionResult failure,
            int passesAlreadyApplied,
            int maxPasses)
        {
            return maxPasses > 0 &&
                   passesAlreadyApplied < maxPasses &&
                   failure != null &&
                   failure.ErrorCode == LlmErrorCode.ContextLengthExceeded;
        }

        /// <inheritdoc />
        [System.Obsolete("Use ShouldRetryAfterContextOverflow(failure, passesAlreadyApplied, maxPasses) instead.")]
        public bool ShouldRetryOnceAfterContextOverflow(LlmCompletionResult failure, bool compactionAlreadyApplied)
        {
            return ShouldRetryAfterContextOverflow(failure, compactionAlreadyApplied ? 1 : 0, 1);
        }
    }
}
