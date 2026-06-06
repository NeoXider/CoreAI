using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Prompts
{
    /// <summary>
    /// Loads agent system prompts from Unity Resources.
    /// </summary>
    public sealed class ResourcesAgentSystemPromptProvider : IAgentSystemPromptProvider
    {
        private readonly string _resourcePathPrefix;

        /// <param name="resourcePathPrefix">The resource path prefix value.</param>
        public ResourcesAgentSystemPromptProvider(string resourcePathPrefix)
        {
            _resourcePathPrefix = resourcePathPrefix?.Trim().TrimEnd('/') ?? "AgentPrompts/System";
        }

        /// <inheritdoc />
        public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
        {
            systemPrompt = null;
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            string path = $"{_resourcePathPrefix}/{roleId.Trim()}";
            TextAsset ta = Resources.Load<TextAsset>(path);
            if (ta == null || string.IsNullOrWhiteSpace(ta.text))
            {
                return false;
            }

            systemPrompt = ta.text;
            return true;
        }
    }
}