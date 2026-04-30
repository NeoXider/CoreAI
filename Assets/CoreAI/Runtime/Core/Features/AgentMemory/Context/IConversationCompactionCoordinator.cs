namespace CoreAI.Ai
{
    /// <summary>
    /// Decides whether the orchestrator may rebuild the prompt with tighter history after a context-overflow failure.
    /// </summary>
    public interface IConversationCompactionCoordinator
    {
        /// <summary>
        /// True when one automatic retry after shrinking the history budget is allowed.
        /// </summary>
        bool ShouldRetryOnceAfterContextOverflow(LlmCompletionResult failure, bool compactionAlreadyApplied);
    }
}
