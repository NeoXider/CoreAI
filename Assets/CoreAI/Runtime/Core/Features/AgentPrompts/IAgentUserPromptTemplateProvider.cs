using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Defines the contract for agent user prompt template provider implementations.
    /// </summary>
    public interface IAgentUserPromptTemplateProvider
    {
        /// <summary>Attempts to resolve the user prompt template for an agent role.</summary>
        bool TryGetUserTemplate(string roleId, out string template);
    }

    /// <summary>Combines multiple user prompt template providers in priority order.</summary>
    public sealed class ChainedAgentUserPromptTemplateProvider : IAgentUserPromptTemplateProvider
    {
        private readonly IReadOnlyList<IAgentUserPromptTemplateProvider> _chain;

        /// <param name="chain">The chain value.</param>
        public ChainedAgentUserPromptTemplateProvider(IReadOnlyList<IAgentUserPromptTemplateProvider> chain)
        {
            _chain = chain;
        }

        /// <inheritdoc />
        public bool TryGetUserTemplate(string roleId, out string template)
        {
            foreach (IAgentUserPromptTemplateProvider p in _chain)
            {
                if (p.TryGetUserTemplate(roleId, out string t) && !string.IsNullOrWhiteSpace(t))
                {
                    template = t;
                    return true;
                }
            }

            template = null;
            return false;
        }
    }

    /// <summary>
    /// Template provider that intentionally returns no user prompt template.
    /// </summary>
    public sealed class NoAgentUserPromptTemplateProvider : IAgentUserPromptTemplateProvider
    {
        /// <inheritdoc />
        public bool TryGetUserTemplate(string roleId, out string template)
        {
            template = null;
            return false;
        }
    }
}
