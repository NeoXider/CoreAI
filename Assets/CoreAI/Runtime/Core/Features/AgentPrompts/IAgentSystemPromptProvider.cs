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
    /// Optional companion contract for prompt providers that can enumerate configured role ids.
    /// </summary>
    public interface IAgentRoleIdProvider
    {
        /// <summary>Returns role ids configured by this provider.</summary>
        IReadOnlyList<string> GetKnownRoleIds();
    }

    /// <summary>
    /// Combines multiple system prompt providers in priority order.
    /// </summary>
    public sealed class ChainedAgentSystemPromptProvider : IAgentSystemPromptProvider, IAgentRoleIdProvider
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

        /// <inheritdoc />
        public IReadOnlyList<string> GetKnownRoleIds()
        {
            List<string> roleIds = new();
            foreach (IAgentSystemPromptProvider provider in _chain)
            {
                if (provider is not IAgentRoleIdProvider roleIdProvider)
                {
                    continue;
                }

                IReadOnlyList<string> known = roleIdProvider.GetKnownRoleIds();
                if (known == null)
                {
                    continue;
                }

                for (int i = 0; i < known.Count; i++)
                {
                    AddUnique(roleIds, known[i]);
                }
            }

            return roleIds;
        }

        private static void AddUnique(List<string> values, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string trimmed = value.Trim();
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == trimmed)
                {
                    return;
                }
            }

            values.Add(trimmed);
        }
    }

    /// <summary>
    /// Built-in fallback system prompt provider for known CoreAI roles.
    /// </summary>
    public sealed class BuiltInDefaultAgentSystemPromptProvider : IAgentSystemPromptProvider, IAgentRoleIdProvider
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

        /// <inheritdoc />
        public IReadOnlyList<string> GetKnownRoleIds()
        {
            return BuiltInAgentRoleIds.AllBuiltInRoles;
        }
    }
}
