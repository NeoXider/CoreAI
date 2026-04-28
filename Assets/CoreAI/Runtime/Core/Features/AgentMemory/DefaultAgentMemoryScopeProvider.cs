namespace CoreAI.Ai
{
    /// <summary>
    /// Default scope provider that keeps existing role-id-only memory behavior.
    /// </summary>
    public sealed class DefaultAgentMemoryScopeProvider : IAgentMemoryScopeProvider
    {
        /// <inheritdoc />
        public AgentMemoryScope GetScope(string roleId)
        {
            return AgentMemoryScope.Empty;
        }
    }
}
