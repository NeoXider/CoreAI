using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Prompts
{
    public sealed class ResourcesUserTemplateProvider : IAgentUserPromptTemplateProvider
    {
        private readonly string _resourcePathPrefix;

        public ResourcesUserTemplateProvider(string resourcePathPrefix)
        {
            _resourcePathPrefix = resourcePathPrefix?.Trim().TrimEnd('/') ?? "AgentPrompts/User";
        }

        public bool TryGetUserTemplate(string roleId, out string template)
        {
            template = null;
            if (string.IsNullOrWhiteSpace(roleId))
                return false;

            var path = $"{_resourcePathPrefix}/{roleId.Trim()}";
            var ta = Resources.Load<TextAsset>(path);
            if (ta == null || string.IsNullOrWhiteSpace(ta.text))
                return false;
            template = ta.text;
            return true;
        }
    }
}
