namespace CoreAI.Ai
{
    /// <summary>
    /// Portable compaction policy for auxiliary LLM calls that fold older transcript into rolling text.
    /// Maps to behaviours described in Kilocode&apos;s compaction/summarize semantics (distinct compactor routing id,
    /// budget headroom delegated to orchestrator&apos;s token policy).
    /// </summary>
    public sealed class LlmContextCompactionOptions
    {
        /// <summary>Routed as <see cref="LlmCompletionRequest.AgentRoleId"/> for compaction calls only.</summary>
        public string CompactorAgentRoleId { get; set; } = BuiltInAgentRoleIds.ContextCompactionAux;

        /// <summary>
        /// System prompt for the compaction-only completion request (<b>not</b> the main agent role system prompt —
        /// that prompt is assembled by the orchestrator and must never appear in compaction input).
        /// </summary>
        public string SystemPrompt { get; set; } = DefaultSystemPrompt;

        /// <summary>Caps summarizer completion length.</summary>
        public int MaxSummaryOutputTokens { get; set; } = 512;

        /// <summary>Sampling temperature for compaction completion.</summary>
        public float Temperature { get; set; } = 0.15f;

        /// <summary>
        /// Maximum total chars in the compaction user payload. Messages exceeding this are truncated.
        /// Default 12 000 (~3 000 tokens) — optimized for fast processing on small models (4B–8B).
        /// </summary>
        public int MaxPayloadChars { get; set; } = 12000;

        /// <summary>
        /// Per-message character limit inside the compaction payload. Longer messages are trimmed with an ellipsis.
        /// Default 800 — game/lesson messages are typically short; tool outputs can be heavily truncated for summaries.
        /// </summary>
        public int MaxPerMessageChars { get; set; } = 800;

        /// <summary>
        /// Maximum chars for the normalized summary output. Longer summaries are truncated with an ellipsis.
        /// Default 4 000 (~1 000 tokens) — keeps the rolling summary compact so it does not bloat the main model's system prompt.
        /// </summary>
        public int MaxSummaryChars { get; set; } = 4000;

        /// <summary>Default system prompt for compaction completions.</summary>
        public static readonly string DefaultSystemPrompt =
            "You compress dialogue into a factual rolling summary for the next model turn.\n" +
            "Rules:\n" +
            "- Preserve user goals, decisions, errors, filenames, identifiers, unresolved questions, and concrete numbers.\n" +
            "- Use short bullet lines; omit pleasantries.\n" +
            "- Fold the \\\"prior summary\\\" plus new messages into ONE updated summary.\n" +
            "- Respond with plain prose only — no preamble or markdown headings.";

        public static LlmContextCompactionOptions Default() => new();
    }
}
