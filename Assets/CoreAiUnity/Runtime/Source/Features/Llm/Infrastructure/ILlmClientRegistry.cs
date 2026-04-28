using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Resolves inner <see cref="ILlmClient"/> instances by agent role.
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

    /// <summary>Rebuilds LLM routing without recreating the VContainer scope.</summary>
    public interface ILlmRoutingController
    {
        /// <summary>Applies a manifest or falls back to the legacy client when routing is disabled.</summary>
        void ApplyManifest(LlmRoutingManifest manifest);
    }
}