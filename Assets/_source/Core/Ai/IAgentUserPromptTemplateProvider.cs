using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Опциональный шаблон «обычного» (user) промпта для роли. Плейсхолдеры: {wave}, {mode}, {party}, {hint}.
    /// </summary>
    public interface IAgentUserPromptTemplateProvider
    {
        bool TryGetUserTemplate(string roleId, out string template);
    }

    public sealed class ChainedAgentUserPromptTemplateProvider : IAgentUserPromptTemplateProvider
    {
        private readonly IReadOnlyList<IAgentUserPromptTemplateProvider> _chain;

        public ChainedAgentUserPromptTemplateProvider(IReadOnlyList<IAgentUserPromptTemplateProvider> chain)
        {
            _chain = chain;
        }

        public bool TryGetUserTemplate(string roleId, out string template)
        {
            foreach (var p in _chain)
            {
                if (p.TryGetUserTemplate(roleId, out var t) && !string.IsNullOrWhiteSpace(t))
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
    /// Всегда false — используется дефолтная сборка user-текста в <see cref="AiPromptComposer"/>.
    /// </summary>
    public sealed class NoAgentUserPromptTemplateProvider : IAgentUserPromptTemplateProvider
    {
        public bool TryGetUserTemplate(string roleId, out string template)
        {
            template = null;
            return false;
        }
    }
}
