using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Опциональный шаблон user-промпта для роли. В <see cref="AiPromptComposer"/> подставляются <c>{telemetry}</c>, <c>{hint}</c>
    /// и плейсхолдеры <c>{ключ}</c> по полям снимка телеметрии.
    /// </summary>
    public interface IAgentUserPromptTemplateProvider
    {
        /// <summary>Шаблон текста; плейсхолдеры заменяет <see cref="AiPromptComposer"/>.</summary>
        bool TryGetUserTemplate(string roleId, out string template);
    }

    /// <summary>Цепочка провайдеров user-шаблонов (первый успешный).</summary>
    public sealed class ChainedAgentUserPromptTemplateProvider : IAgentUserPromptTemplateProvider
    {
        private readonly IReadOnlyList<IAgentUserPromptTemplateProvider> _chain;

        /// <param name="chain">Порядок приоритета провайдеров.</param>
        public ChainedAgentUserPromptTemplateProvider(IReadOnlyList<IAgentUserPromptTemplateProvider> chain)
        {
            _chain = chain;
        }

        /// <inheritdoc />
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
        /// <inheritdoc />
        public bool TryGetUserTemplate(string roleId, out string template)
        {
            template = null;
            return false;
        }
    }
}
