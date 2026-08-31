using System;
using System.Collections.Generic;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Prompts
{
    /// <summary>
    /// Loads agent system prompts from Unity Resources.
    /// </summary>
    public sealed class ResourcesAgentSystemPromptProvider : IAgentSystemPromptProvider
    {
        private readonly Dictionary<string, string> _prompts;

        /// <param name="resourcePathPrefix">The resource path prefix value.</param>
        public ResourcesAgentSystemPromptProvider(string resourcePathPrefix)
        {
            string resourcePathPrefixValue =
                resourcePathPrefix?.Trim().TrimEnd('/') ?? "AgentPrompts/System";
            _prompts = new Dictionary<string, string>(StringComparer.Ordinal);
            TextAsset[] assets = Resources.LoadAll<TextAsset>(resourcePathPrefixValue);
            foreach (TextAsset asset in assets)
            {
                if (asset != null && !string.IsNullOrWhiteSpace(asset.text))
                {
                    _prompts[asset.name] = asset.text;
                }
            }
        }

        /// <inheritdoc />
        public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
        {
            systemPrompt = null;
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            return _prompts.TryGetValue(roleId.Trim(), out systemPrompt);
        }
    }
}
