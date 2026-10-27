namespace CoreAI.Ai
{
    /// <summary>
    /// Resolves the current memory scope used to isolate role memory in product projects.
    /// </summary>
    public interface IAgentMemoryScopeProvider
    {
        /// <summary>
        /// Returns the current memory scope. Return <see cref="AgentMemoryScope.Empty"/> to preserve role-only keys.
        /// </summary>
        AgentMemoryScope GetScope(string roleId);
    }

    /// <summary>Exposes a known admission scope provider without querying its current scope.</summary>
    public interface IAgentMemoryScopeProviderSource
    {
        /// <summary>Returns a nonnull known provider, or false with null when its provider is unknown.</summary>
        bool TryGetMemoryScopeProvider(out IAgentMemoryScopeProvider provider);
    }
}
