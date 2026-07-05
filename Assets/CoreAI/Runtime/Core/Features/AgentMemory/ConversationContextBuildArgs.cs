namespace CoreAI.Ai
{
    /// <summary>Optional build parameters for <see cref="IConversationContextManager"/> (per orchestrator request).</summary>
    public sealed class ConversationContextBuildArgs
    {
        /// <summary>Maximum estimated tokens for recent chat messages after compaction.</summary>
        public int HistoryTokenBudget { get; set; }

        /// <summary>Filled before snapshot build for telemetry / tracing.</summary>
        public ContextBudget? SourceBudget { get; set; }

        /// <summary>
        /// When true (and the host registered an LLM-assisted pipeline), evicted history may be summarized via an auxiliary LLM call.
        /// Set from <see cref="ICoreAISettings.EnableLlmContextCompaction"/> and <see cref="AgentMemoryPolicy.RoleMemoryConfig.UseLlmContextCompaction"/>.
        /// </summary>
        public bool UseLlmContextCompaction { get; set; }

        /// <summary>
        /// When greater than zero, caps rolled summary text to roughly this many estimated tokens before persistence.
        /// </summary>
        public int MaxRolledSummaryTokens { get; set; }

        /// <summary>
        /// Compaction (summarization of older turns) only triggers once estimated history tokens reach this
        /// fraction of the history budget; below it, all turns are kept verbatim and the stored summary is left untouched.
        /// Roadmap §2. Invalid values fall back to <see cref="CoreAISettings.DefaultConversationCompactionTriggerRatio"/>.
        /// </summary>
        public float CompactionTriggerRatio { get; set; }

        /// <summary>
        /// When true, roadmap §7 context editing prunes stale prompt-history entries before budget partitioning.
        /// </summary>
        public bool EnableContextPruning { get; set; }

        /// <summary>
        /// Maximum newest durable <c>tool</c> / <c>## Tool Results</c> messages retained in the prompt history copy.
        /// </summary>
        public int MaxRetainedToolResultMessages { get; set; }
    }
}