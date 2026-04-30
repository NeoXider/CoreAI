namespace CoreAI.Ai
{
    /// <inheritdoc />
    public sealed class DefaultConversationCompactionCoordinator : IConversationCompactionCoordinator
    {
        /// <inheritdoc />
        public bool ShouldRetryOnceAfterContextOverflow(LlmCompletionResult failure, bool compactionAlreadyApplied)
        {
            return !compactionAlreadyApplied && failure != null &&
                   failure.ErrorCode == LlmErrorCode.ContextLengthExceeded;
        }
    }
}
