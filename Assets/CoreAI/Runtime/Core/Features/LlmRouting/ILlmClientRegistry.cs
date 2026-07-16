namespace CoreAI.Ai
{
    /// <summary>
    /// Atomic route resolution for one request: client and routing metadata observed together,
    /// so a concurrent endpoint/profile switch cannot yield a client from one endpoint annotated
    /// with another endpoint's profile id, context window, or execution mode.
    /// </summary>
    public sealed class LlmRoleRouteSnapshot
    {
        public ILlmClient Client { get; set; }
        public string ProfileId { get; set; } = "";
        public int ContextWindowTokens { get; set; }
        public LlmExecutionMode Mode { get; set; }

        /// <summary>
        /// True when a real profile (runtime or manifest) matched; false when <see cref="ProfileId"/>
        /// is the reserved "fallback" diagnostic and the legacy backend serves the request, in which
        /// case <see cref="ContextWindowTokens"/> is a default rather than endpoint knowledge.
        /// </summary>
        public bool IsRouted { get; set; }
    }

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

        /// <summary>
        /// Resolves client, profile id, context window, and execution mode as one consistent
        /// observation. Registries with mutable routing state should override this to compute
        /// all four under a single lock acquisition.
        /// </summary>
        LlmRoleRouteSnapshot ResolveRouteForRole(string roleId, string explicitProfileId)
        {
            string profileId = ResolveProfileIdForRole(roleId, explicitProfileId);
            return new LlmRoleRouteSnapshot
            {
                Client = ResolveClientForRole(roleId, explicitProfileId),
                ProfileId = profileId,
                ContextWindowTokens = ResolveContextWindowForRole(roleId, explicitProfileId),
                Mode = ResolveExecutionModeForRole(roleId, explicitProfileId),
                // WHY: best effort for registries without profile-existence knowledge; the concrete
                // LlmClientRegistry overrides this with an exact profile lookup.
                IsRouted = !string.IsNullOrEmpty(profileId) &&
                           !string.Equals(profileId, "fallback", System.StringComparison.Ordinal)
            };
        }

        /// <summary>
        /// Reports an endpoint-level request failure observed on a routed profile (expired credentials,
        /// unreachable backend) so registries can surface degraded health on the endpoint snapshot
        /// instead of keeping a stale Ready state until restart. Default: no-op for legacy registries.
        /// </summary>
        void ReportRouteFailure(string profileId, LlmErrorCode errorCode, string error)
        {
        }
    }
}
