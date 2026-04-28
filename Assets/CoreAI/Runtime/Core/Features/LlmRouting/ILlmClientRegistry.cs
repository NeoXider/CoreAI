namespace CoreAI.Ai
{
    /// <summary>
    /// Portable contract for resolving an LLM client and routing metadata by agent role.
    /// </summary>
    public interface ILlmClientRegistry
    {
        /// <summary>Inner client for a role before outer logging decorators.</summary>
        ILlmClient ResolveClientForRole(string roleId);

        /// <summary>Context window in tokens for the role route.</summary>
        int ResolveContextWindowForRole(string roleId);

        /// <summary>Product-facing execution mode for the role route.</summary>
        LlmExecutionMode ResolveExecutionModeForRole(string roleId);

        /// <summary>Routing profile id for the role route.</summary>
        string ResolveProfileIdForRole(string roleId);
    }
}
