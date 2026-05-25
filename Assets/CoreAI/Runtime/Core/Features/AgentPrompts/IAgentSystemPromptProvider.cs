using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Defines the contract for agent system prompt provider implementations.
    /// </summary>
    public interface IAgentSystemPromptProvider
    {
        /// <summary>Attempts to resolve the base system prompt for an agent role.</summary>
        bool TryGetSystemPrompt(string roleId, out string systemPrompt);
    }

    /// <summary>
    /// Combines multiple system prompt providers in priority order.
    /// </summary>
    public sealed class ChainedAgentSystemPromptProvider : IAgentSystemPromptProvider
    {
        private readonly IReadOnlyList<IAgentSystemPromptProvider> _chain;

        /// <param name="chain">The chain value.</param>
        public ChainedAgentSystemPromptProvider(IReadOnlyList<IAgentSystemPromptProvider> chain)
        {
            _chain = chain;
        }

        /// <inheritdoc />
        public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
        {
            foreach (IAgentSystemPromptProvider p in _chain)
            {
                if (p.TryGetSystemPrompt(roleId, out string s) && !string.IsNullOrWhiteSpace(s))
                {
                    systemPrompt = s.Trim();
                    return true;
                }
            }

            systemPrompt = null;
            return false;
        }
    }

    /// <summary>
    /// Built-in fallback system prompt provider for known CoreAI roles.
    /// </summary>
    public sealed class BuiltInDefaultAgentSystemPromptProvider : IAgentSystemPromptProvider
    {
        /// <inheritdoc />
        public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
        {
            string basePrompt = roleId switch
            {
                BuiltInAgentRoleIds.Creator => BuiltInAgentSystemPromptTexts.Creator,
                BuiltInAgentRoleIds.Analyzer => BuiltInAgentSystemPromptTexts.Analyzer,
                BuiltInAgentRoleIds.Programmer => BuiltInAgentSystemPromptTexts.Programmer,
                BuiltInAgentRoleIds.AiNpc => BuiltInAgentSystemPromptTexts.AiNpc,
                BuiltInAgentRoleIds.CoreMechanic => BuiltInAgentSystemPromptTexts.CoreMechanic,
                BuiltInAgentRoleIds.PlainChat => BuiltInAgentSystemPromptTexts.PlainChat,
                BuiltInAgentRoleIds.SmartChat => BuiltInAgentSystemPromptTexts.SmartChat,
                BuiltInAgentRoleIds.Merchant => BuiltInAgentSystemPromptTexts.Merchant,
                _ =>
                    $"You are agent \"{roleId}\" in CoreAI. Follow the user message and any session hints; prefer structured output when the game requests it."
            };
            // or every orchestrator request duplicates it (built-in strings are raw role prompts).
            systemPrompt = basePrompt;
            return true;
        }
    }
}
