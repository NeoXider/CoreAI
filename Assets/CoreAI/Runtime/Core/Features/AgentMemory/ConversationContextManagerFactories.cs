namespace CoreAI.Ai
{
    /// <summary>
    /// Portable factory for <see cref="IConversationContextManager"/> implementations (no DI attributes).
    /// Hosts (Unity <c>CorePortableInstaller</c> or console tests) call this to honour <see cref="ICoreAISettings.EnableLlmContextCompaction"/>.
    /// </summary>
    public static class ConversationContextManagerFactories
    {
        /// <summary>
        /// When <paramref name="enableLlmContextCompaction"/> is true and <paramref name="llmClient"/> is non-null, returns
        /// <see cref="SelectingConversationContextManager"/> so each request can choose LLM vs deterministic compaction via
        /// <see cref="ConversationContextBuildArgs.UseLlmContextCompaction"/>.
        /// Otherwise returns <see cref="DeterministicConversationContextManager"/>.
        /// </summary>
        public static IConversationContextManager Create(
            bool enableLlmContextCompaction,
            IConversationSummaryStore summaryStore,
            ITokenEstimator tokenEstimator,
            ILlmClient llmClient,
            LlmContextCompactionOptions compactionOptions = null)
        {
            ITokenEstimator estimator = tokenEstimator ?? new HeuristicTokenEstimator();
            if (enableLlmContextCompaction && llmClient != null)
            {
                return new SelectingConversationContextManager(
                    summaryStore,
                    estimator,
                    llmClient,
                    compactionOptions ?? LlmContextCompactionOptions.Default());
            }

            return new DeterministicConversationContextManager(summaryStore, estimator);
        }
    }
}