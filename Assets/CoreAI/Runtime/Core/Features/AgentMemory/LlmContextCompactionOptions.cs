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
        /// System prompt text associated with this entry.
        /// </summary>
        public string SystemPrompt { get; set; } = DefaultSystemPrompt;

        /// <summary>Caps summarizer completion length.</summary>
        public int MaxSummaryOutputTokens { get; set; } = 512;

        /// <summary>Sampling temperature for compaction completion.</summary>
        public float Temperature { get; set; } = 0.15f;

        /// <summary>
        /// Maximum payload chars.
        /// </summary>
        public int MaxPayloadChars { get; set; } = 12000;

        /// <summary>
        /// Maximum per message chars.
        /// </summary>
        public int MaxPerMessageChars { get; set; } = 800;

        /// <summary>
        /// Maximum summary chars.
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

        public static LlmContextCompactionOptions Default()
        {
            return new LlmContextCompactionOptions();
        }
    }
}
