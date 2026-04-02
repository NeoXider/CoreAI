using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Поставщик системного промпта по id роли. Реализации: файлы Resources (Unity), манифест, встроенные fallback.
    /// </summary>
    public interface IAgentSystemPromptProvider
    {
        bool TryGetSystemPrompt(string roleId, out string systemPrompt);
    }

    /// <summary>
    /// Цепочка: первый провайдер, вернувший текст, побеждает.
    /// </summary>
    public sealed class ChainedAgentSystemPromptProvider : IAgentSystemPromptProvider
    {
        private readonly IReadOnlyList<IAgentSystemPromptProvider> _chain;

        public ChainedAgentSystemPromptProvider(IReadOnlyList<IAgentSystemPromptProvider> chain)
        {
            _chain = chain;
        }

        public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
        {
            foreach (var p in _chain)
            {
                if (p.TryGetSystemPrompt(roleId, out var s) && !string.IsNullOrWhiteSpace(s))
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
    /// Встроенные тексты, если нет файлов и манифеста.
    /// </summary>
    public sealed class BuiltInDefaultAgentSystemPromptProvider : IAgentSystemPromptProvider
    {
        public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
        {
            systemPrompt = roleId switch
            {
                BuiltInAgentRoleIds.Creator => BuiltInAgentSystemPromptTexts.Creator,
                BuiltInAgentRoleIds.Analyzer => BuiltInAgentSystemPromptTexts.Analyzer,
                BuiltInAgentRoleIds.Programmer => BuiltInAgentSystemPromptTexts.Programmer,
                BuiltInAgentRoleIds.AiNpc => BuiltInAgentSystemPromptTexts.AiNpc,
                BuiltInAgentRoleIds.CoreMechanic => BuiltInAgentSystemPromptTexts.CoreMechanic,
                BuiltInAgentRoleIds.PlayerChat => BuiltInAgentSystemPromptTexts.PlayerChat,
                _ =>
                    $"You are agent \"{roleId}\" in CoreAI. Follow the user message and any session hints; prefer structured output when the game requests it."
            };
            return true;
        }
    }
}
