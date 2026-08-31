using System;
using System.Collections.Generic;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Prompts
{
    /// <summary>
    /// Loads user prompt templates from Unity Resources.
    /// </summary>
    public sealed class ResourcesUserTemplateProvider : IAgentUserPromptTemplateProvider
    {
        private readonly Dictionary<string, string> _templates;

        /// <param name="resourcePathPrefix">The resource path prefix value.</param>
        public ResourcesUserTemplateProvider(string resourcePathPrefix)
        {
            string resourcePathPrefixValue =
                resourcePathPrefix?.Trim().TrimEnd('/') ?? "AgentPrompts/User";
            _templates = new Dictionary<string, string>(StringComparer.Ordinal);
            TextAsset[] assets = Resources.LoadAll<TextAsset>(resourcePathPrefixValue);
            foreach (TextAsset asset in assets)
            {
                if (asset != null && !string.IsNullOrWhiteSpace(asset.text))
                {
                    _templates[asset.name] = asset.text;
                }
            }
        }

        /// <inheritdoc />
        public bool TryGetUserTemplate(string roleId, out string template)
        {
            template = null;
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            return _templates.TryGetValue(roleId.Trim(), out template);
        }
    }
}
