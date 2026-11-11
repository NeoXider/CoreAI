namespace CoreAI.Ai
{
    /// <summary>
    /// The decision protocol an endpoint speaks next to (or instead of) chat. A decision model answers typed
    /// questions with per-option probabilities instead of text; the portable contract is
    /// <c>CoreAI.Decisions.IDecisionClient</c>. <see cref="None"/> keeps an endpoint chat-only, and it is what a
    /// descriptor persisted before this member existed deserialises to, so decision routing refuses such an
    /// endpoint with a named reason instead of guessing.
    /// </summary>
    public enum LlmDecisionProtocol
    {
        /// <summary>Chat-only endpoint; decision routing refuses it.</summary>
        None = 0,

        /// <summary>
        /// The System One decision route, <c>POST {base}/systemone</c>: llama.cpp <c>llama-server</c>, Ollama,
        /// LM Studio and TypeSafe. Probabilities come from the model itself (calibrated flag set).
        /// </summary>
        SystemOne = 1,

        /// <summary>
        /// OpenRouter decision models (<c>typesafe/jev-*</c>): its System One route or its alpha decisions route,
        /// chosen by an option on the client.
        /// </summary>
        OpenRouter = 2,

        /// <summary>
        /// An ordinary OpenAI-compatible chat model asked through single-token labels and <c>top_logprobs</c>.
        /// Works with any chat server that returns logprobs; the probabilities are marked uncalibrated.
        /// </summary>
        ChatLogprobs = 3
    }
}
