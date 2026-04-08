using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>Вход одного вызова <see cref="ILlmClient.CompleteAsync"/> (роль, промпты, трассировка).</summary>
    public sealed class LlmCompletionRequest
    {
        /// <summary>Роль для маршрутизации бэкенда и системного промпта.</summary>
        public string AgentRoleId { get; set; } = "";

        /// <summary>Системная инструкция модели.</summary>
        public string SystemPrompt { get; set; } = "";

        /// <summary>User-часть (опционально, если ChatHistory не используется или дополняет его).</summary>
        public string UserPayload { get; set; } = "";

        /// <summary>История чата (опционально). Если задана, используется для MEAI.</summary>
        public IList<Microsoft.Extensions.AI.ChatMessage> ChatHistory { get; set; }

        /// <summary>Сквозной id для логов (оркестратор / декоратор LLM / роутер команд).</summary>
        public string TraceId { get; set; } = "";

        /// <summary>Краткая метка выбранного бэкенда после маршрутизации по роли (для логов LLM).</summary>
        public string RoutingProfileId { get; set; } = "";

        /// <summary>Бюджет контекста в токенах для роли (по умолчанию 8192, может быть переопределен маршрутизацией).</summary>
        public int ContextWindowTokens { get; set; } = 8192;

        /// <summary>Опциональный лимит токенов ответа модели.</summary>
        public int? MaxOutputTokens { get; set; }

        /// <summary>Температура генерации (по умолчанию 0.1, переопределяется на уровне агента).</summary>
        public float Temperature { get; set; } = 0.1f;

        /// <summary>Инструменты (tools), доступные модели для вызова.</summary>
        public IReadOnlyList<ILlmTool> Tools { get; set; }
    }

    /// <summary>Результат вызова модели: текст ответа, ошибка и опционально usage-токены.</summary>
    public sealed class LlmCompletionResult
    {
        /// <summary>При <c>false</c> смотреть <see cref="Error"/>.</summary>
        public bool Ok { get; set; }

        /// <summary>Сырой ответ модели (текст/JSON конверта).</summary>
        public string Content { get; set; } = "";

        /// <summary>Описание сбоя или отмены.</summary>
        public string Error { get; set; } = "";

        /// <summary>Заполняется OpenAI-compatible HTTP при наличии <c>usage</c> в JSON.</summary>
        public int? PromptTokens { get; set; }

        /// <summary>Токены completion из usage (HTTP).</summary>
        public int? CompletionTokens { get; set; }

        /// <summary>Суммарные токены из usage (HTTP).</summary>
        public int? TotalTokens { get; set; }
    }

    /// <summary>
    /// Абстракция вызова модели (DGF_SPEC §5.2, §7). Реализации — в Core (stub) и Unity-слое (LLMUnity, OpenAI-compatible HTTP).
    /// </summary>
    public interface ILlmClient
    {
        /// <summary>Один запрос к модели; поддерживает отмену и таймауты снаружи (декоратор).</summary>
        Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>Установить инструменты (tools), доступные модели для вызова. Default: no-op.</summary>
        virtual void SetTools(IReadOnlyList<ILlmTool> tools)
        {
        }
    }
}