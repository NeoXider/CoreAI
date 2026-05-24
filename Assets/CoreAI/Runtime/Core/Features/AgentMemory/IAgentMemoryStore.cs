namespace CoreAI.Ai
{
    /// <summary>IAgentMemoryStore interface.</summary>
    public interface IAgentMemoryStore
    {
        /// <summary>Attempts to load persisted memory state for the requested role.</summary>
        bool TryLoad(string roleId, out AgentMemoryState state);

        /// <summary>Persists memory state for the requested role.</summary>
        void Save(string roleId, AgentMemoryState state);

        /// <summary>Clears all memory state for the requested role.</summary>
        void Clear(string roleId);

        /// <summary>Clears only the chat history stored for the requested role.</summary>
        void ClearChatHistory(string roleId);

        /// <summary>
/// Executes AppendChatMessage API operation.
        ///
        /// </summary>
        /// <param name="roleId">The role id value.</param>
        /// <param name="role">The role value.</param>
        /// <param name="content">The content value.</param>
        /// <param name="persistToDisk">The persist to disk value.</param>
        void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true);

        /// <summary>
/// Executes GetChatHistory API operation.
        /// </summary>
        /// <param name="roleId">The role id value.</param>
        /// <param name="maxMessages">The max messages value.</param>
        /// <returns>The operation result.</returns>
        ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0);
    }

    /// <summary>ChatMessage struct.</summary>
    [System.Serializable]
    public struct ChatMessage
    {
        public string Role; // "user" | "assistant" | "system"
        public string Content;
        public long Timestamp; // Unix timestamp

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
            Timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}
