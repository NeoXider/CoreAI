namespace CoreAI.Ai
{
    /// <summary>No automatic retry after context overflow.</summary>
    public sealed class NullConversationCompactionCoordinator : IConversationCompactionCoordinator
    {
        /// <inheritdoc />
        public bool ShouldRetryAfterContextOverflow(
            LlmCompletionResult failure,
            int passesAlreadyApplied,
            int maxPasses)
        {
            return false;
        }

        /// <inheritdoc />
        [System.Obsolete("Use ShouldRetryAfterContextOverflow(failure, passesAlreadyApplied, maxPasses) instead.")]
        public bool ShouldRetryOnceAfterContextOverflow(LlmCompletionResult failure, bool compactionAlreadyApplied)
        {
            return false;
        }
    }
}
