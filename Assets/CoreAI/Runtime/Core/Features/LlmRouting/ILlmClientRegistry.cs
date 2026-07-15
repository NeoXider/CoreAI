namespace CoreAI.Ai
{
    /// <summary>
    /// Portable contract for resolving an LLM client and routing metadata by agent role.
    /// </summary>
    public interface ILlmClientRegistry
    {
        /// <summary>Inner client for a role before outer logging decorators.</summary>
        ILlmClient ResolveClientForRole(string roleId);

        /// <summary>
        /// Resolves a client using an explicit profile when supplied. Implementations with dynamic
        /// profiles override this overload; legacy registries retain role-only behaviour.
        /// </summary>
        ILlmClient ResolveClientForRole(string roleId, string explicitProfileId)
        {
            return ResolveClientForRole(roleId);
        }

        /// <summary>Context window in tokens for the role route.</summary>
        int ResolveContextWindowForRole(string roleId);

        /// <summary>Context window resolved with an optional explicit profile.</summary>
        int ResolveContextWindowForRole(string roleId, string explicitProfileId)
        {
            return ResolveContextWindowForRole(roleId);
        }

        /// <summary>Product-facing execution mode for the role route.</summary>
        LlmExecutionMode ResolveExecutionModeForRole(string roleId);

        /// <summary>Execution mode resolved with an optional explicit profile.</summary>
        LlmExecutionMode ResolveExecutionModeForRole(string roleId, string explicitProfileId)
        {
            return ResolveExecutionModeForRole(roleId);
        }

        /// <summary>Routing profile id for the role route.</summary>
        string ResolveProfileIdForRole(string roleId);

        /// <summary>Effective profile id resolved with an optional explicit profile.</summary>
        string ResolveProfileIdForRole(string roleId, string explicitProfileId)
        {
            return ResolveProfileIdForRole(roleId);
        }
    }
}
