namespace CoreAI.Ai
{
    /// <summary>Persistence contract for role-scoped agent memory and chat history.</summary>
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
        /// Appends one chat message to a role's persisted conversation history.
        /// </summary>
        /// <param name="roleId">Agent role id that owns the history.</param>
        /// <param name="role">Message role, such as <c>user</c>, <c>assistant</c>, or <c>system</c>.</param>
        /// <param name="content">Message text.</param>
        /// <param name="persistToDisk">Whether the store should flush the change immediately.</param>
        void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true);

        /// <summary>
        /// Returns recent chat history for a role.
        /// </summary>
        /// <param name="roleId">Agent role id that owns the history.</param>
        /// <param name="maxMessages">Maximum messages to return; <c>0</c> means store default/all.</param>
        /// <returns>Chat messages in chronological order as provided by the store.</returns>
        ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0);
    }

    /// <summary>Serializable chat transcript entry.</summary>
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
