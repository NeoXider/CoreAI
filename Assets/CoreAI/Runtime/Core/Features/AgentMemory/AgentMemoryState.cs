namespace CoreAI.Ai
{
    /// <summary>
    /// Provides agent memory state functionality.
    /// </summary>
    public sealed class AgentMemoryState
    {
        /// <summary>
        /// Last system prompt.
        /// </summary>
        public string LastSystemPrompt { get; set; }

        /// <summary>
        /// Memory.
        /// </summary>
        public string Memory { get; set; }
    }
}
