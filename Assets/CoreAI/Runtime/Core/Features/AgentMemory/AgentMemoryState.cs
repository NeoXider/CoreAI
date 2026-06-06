namespace CoreAI.Ai
{
    /// <summary>
    /// Serializable role memory payload stored by <see cref="IAgentMemoryStore"/>.
    /// </summary>
    public sealed class AgentMemoryState
    {
        /// <summary>
        /// Last composed system prompt associated with the stored memory.
        /// </summary>
        public string LastSystemPrompt { get; set; }

        /// <summary>
        /// Durable memory text accumulated for the role.
        /// </summary>
        public string Memory { get; set; }
    }
}