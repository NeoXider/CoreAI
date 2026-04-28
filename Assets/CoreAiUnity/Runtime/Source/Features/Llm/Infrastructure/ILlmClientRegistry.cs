namespace CoreAI.Infrastructure.Llm
{
    /// <summary>Rebuilds LLM routing without recreating the VContainer scope.</summary>
    public interface ILlmRoutingController
    {
        /// <summary>Applies a manifest or falls back to the legacy client when routing is disabled.</summary>
        void ApplyManifest(LlmRoutingManifest manifest);
    }
}