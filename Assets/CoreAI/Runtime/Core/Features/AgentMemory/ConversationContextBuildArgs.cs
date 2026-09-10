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
        /// Estimated tokens reserved for the rolling summary in the outgoing request, on top of
        /// <see cref="HistoryTokenBudget"/> (which bounds the recent tail only). Zero when the caller reserved
        /// nothing. WHY the manager does NOT apply this: it bounds what is STORED, and the store must keep
        /// the whole retelling; the ORCHESTRATOR applies this reserve to the copy it sends.
        /// </summary>
        public int SummaryTokenBudget { get; set; }

        /// <summary>
        /// When greater than zero, an explicit cap on rolled summary text (estimated tokens) before persistence.
        /// Zero means "no explicit cap" for the STORED summary, and the manager honours that literally.
        /// It does not mean the request is unbounded: the orchestrator bounds the copy it sends to
        /// <see cref="SummaryTokenBudget"/>, leaving the stored text whole.
        /// </summary>
        public int MaxRolledSummaryTokens { get; set; }

        /// <summary>
        /// Compaction (summarization of older turns) only triggers once estimated history tokens reach this
        /// fraction of the history budget; below it, all turns are kept verbatim and the stored summary is left untouched.
        /// Roadmap §2. Invalid values fall back to <see cref="CoreAISettings.DefaultConversationCompactionTriggerRatio"/>.
        /// </summary>
        public float CompactionTriggerRatio { get; set; }

        /// <summary>
        /// When true, roadmap §7 context editing prunes stale prompt-history entries on the emitted recent
        /// tail AFTER budget partitioning and compaction — never before. Compaction must fold the full
        /// prefix, including messages the pruner would discard, or they would vanish from every future
        /// prompt without a trace.
        /// </summary>
        public bool EnableContextPruning { get; set; }

        /// <summary>
        /// Maximum newest durable <c>tool</c> / <c>## Tool Results</c> messages retained in the prompt history copy.
        /// </summary>
        public int MaxRetainedToolResultMessages { get; set; }

        /// <summary>
        /// Defers summary persistence to the snapshot owner. The owner must commit old-history summaries
        /// before provider dispatch or any history mutation that could discard their source messages.
        /// </summary>
        public bool DeferSummaryPersistence { get; set; }
    }
}
