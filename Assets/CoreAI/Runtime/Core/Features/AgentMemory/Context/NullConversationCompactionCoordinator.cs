namespace CoreAI.Ai
{
    /// <summary>No automatic retry after context overflow.</summary>
    public sealed class NullConversationCompactionCoordinator : IConversationCompactionCoordinator
    {
        /// <inheritdoc />
        public bool ShouldRetryOnceAfterContextOverflow(LlmCompletionResult failure, bool compactionAlreadyApplied)
        {
            return false;
        }
    }
}