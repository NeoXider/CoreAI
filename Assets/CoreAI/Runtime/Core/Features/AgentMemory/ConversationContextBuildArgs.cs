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
    }
}
