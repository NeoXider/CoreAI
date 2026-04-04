using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Prompts
{
    /// <summary>
    /// Загрузка <c>Resources/{root}/&lt;roleId&gt;</c> как TextAsset (без .txt в пути).
    /// </summary>
    public sealed class ResourcesAgentSystemPromptProvider : IAgentSystemPromptProvider
    {
        private readonly string _resourcePathPrefix;

        /// <param name="resourcePathPrefix">Корень в Resources, по умолчанию <c>AgentPrompts/System</c>.</param>
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