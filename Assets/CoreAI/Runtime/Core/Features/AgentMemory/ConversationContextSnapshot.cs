namespace CoreAI.Ai
{
    /// <summary>
    /// Result of preparing long-running conversation context for an LLM request.
    /// </summary>
    public sealed class ConversationContextSnapshot
    {
        /// <summary>Summary of older messages that were compacted out of the live chat window.</summary>
        public string Summary { get; set; } = "";

        /// <summary>Recent messages that should still be sent as chat history.</summary>
        public ChatMessage[] RecentMessages { get; set; } = System.Array.Empty<ChatMessage>();

        /// <summary>True when older history was compacted into <see cref="Summary"/>.</summary>
        public bool WasCompacted { get; set; }
    }
}
