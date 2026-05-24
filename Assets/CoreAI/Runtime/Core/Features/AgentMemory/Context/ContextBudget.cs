namespace CoreAI.Ai
{
    /// <summary>Budget buckets for fitting prompts into a model context window (opaque estimates).</summary>
    public readonly struct ContextBudget
    {
        /// <summary>Role/profile context window ceiling in tokens.</summary>
        public int MaxContextTokens { get; }

        /// <summary>Reserved for completion output (estimated).</summary>
        public int ReservedForCompletion { get; }

        /// <summary>Estimated tokens for fixed prompt: system (pre-summary) + user + tools contract.</summary>
        public int EstimatedFixedPromptTokens { get; }

        /// <summary>Budget allocated to serialized chat history after compaction.</summary>
        public int HistoryTokenBudget { get; }

        /// <summary>Estimated slack / overhead (tool schema, formatting).</summary>
        public int ReservedSlackTokens { get; }

        /// <summary>Creates a context budget snapshot (Unity-compatible: no init-only accessors).</summary>
        public ContextBudget(
            int maxContextTokens,
            int reservedForCompletion,
            int estimatedFixedPromptTokens,
            int historyTokenBudget,
            int reservedSlackTokens)
        {
            MaxContextTokens = maxContextTokens;
            ReservedForCompletion = reservedForCompletion;
            EstimatedFixedPromptTokens = estimatedFixedPromptTokens;
            HistoryTokenBudget = historyTokenBudget;
            ReservedSlackTokens = reservedSlackTokens;
        }
    }
}