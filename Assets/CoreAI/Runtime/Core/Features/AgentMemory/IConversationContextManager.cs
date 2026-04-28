namespace CoreAI.Ai
{
    /// <summary>
    /// Prepares chat history for an LLM request by keeping recent turns and optionally summarizing older context.
    /// </summary>
    public interface IConversationContextManager
    {
        /// <summary>
        /// Builds a context snapshot for the current request.
        /// </summary>
        ConversationContextSnapshot BuildSnapshot(
            string roleId,
            ChatMessage[] history,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig);
    }
}
