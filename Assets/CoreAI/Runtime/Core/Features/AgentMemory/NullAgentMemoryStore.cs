namespace CoreAI.Ai
{
    /// <summary>Agent memory store used when persistence is unavailable.</summary>
    public sealed class NullAgentMemoryStore : IAgentMemoryStore
    {
        /// <inheritdoc />
        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            state = null;
            return false;
        }

        public void Save(string roleId, AgentMemoryState state)
        {
        }

        /// <inheritdoc />
        public void Clear(string roleId)
        {
        }

        public void ClearChatHistory(string roleId)
        {
        }

        public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
        {
        }

        public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
        {
            return System.Array.Empty<ChatMessage>();
        }
    }
}