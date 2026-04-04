using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Prompts
{
    /// <summary>
    /// Шаблоны user-сообщений из <c>Resources</c>: путь <c>{prefix}/{roleId}</c> (TextAsset без расширения в пути).
    /// </summary>
    public sealed class ResourcesUserTemplateProvider : IAgentUserPromptTemplateProvider
    {
        private readonly string _resourcePathPrefix;

        /// <param name="resourcePathPrefix">Корень в Resources, по умолчанию <c>AgentPrompts/User</c>.</param>
        public ResourcesUserTemplateProvider(string resourcePathPrefix)
        {
            _resourcePathPrefix = resourcePathPrefix?.Trim().TrimEnd('/') ?? "AgentPrompts/User";
        }

        /// <inheritdoc />
        public bool TryGetUserTemplate(string roleId, out string template)
        {
            template = null;
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

            template = ta.text;
            return true;
        }
    }
}