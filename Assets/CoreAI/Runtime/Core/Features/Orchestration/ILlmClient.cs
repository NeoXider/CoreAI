using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Stable error category for LLM failures that UI, retry policies, and backend integrations can handle without parsing strings.
    /// </summary>
    public enum LlmErrorCode
    {
        /// <summary>No error was reported.</summary>
        None = 0,

        /// <summary>The request exceeded its timeout.</summary>
        Timeout = 1,

        /// <summary>The caller cancelled the request.</summary>
        Cancelled = 2,

        /// <summary>The provider returned no usable content.</summary>
        EmptyResponse = 3,

        /// <summary>Authorization failed or the token has expired.</summary>
        AuthExpired = 4,

        /// <summary>The backend or local limiter rejected the request because quota was exhausted.</summary>
        QuotaExceeded = 5,

        /// <summary>The provider or backend rate limited the request.</summary>
        RateLimited = 6,

        /// <summary>The provider or backend is unavailable.</summary>
        BackendUnavailable = 7,

        /// <summary>The request was malformed or rejected by validation.</summary>
        InvalidRequest = 8,

        /// <summary>The provider returned an error that does not fit a more specific category.</summary>
        ProviderError = 9,

        /// <summary>Routing could not resolve a usable backend.</summary>
        RoutingError = 10
    }

    /// <summary>
    /// Exception type used by LLM adapters to preserve structured failure details across abstraction layers.
    /// </summary>
    public sealed class LlmClientException : Exception
    {
        /// <summary>Creates an exception with structured LLM failure metadata.</summary>
        public LlmClientException(
            string message,
            LlmErrorCode errorCode = LlmErrorCode.ProviderError,
            int? httpStatus = null,
            int? retryAfterSeconds = null,
            string providerErrorBody = null)
            : base(message ?? "")
        {
            ErrorCode = errorCode;
            HttpStatus = httpStatus;
            RetryAfterSeconds = retryAfterSeconds;
            ProviderErrorBody = providerErrorBody ?? "";
        }

        /// <summary>Stable failure category.</summary>
        public LlmErrorCode ErrorCode { get; }

        /// <summary>HTTP status code when the failure came from an HTTP transport.</summary>
        public int? HttpStatus { get; }

        /// <summary>Provider hint for when the request may be retried.</summary>
        public int? RetryAfterSeconds { get; }

        /// <summary>Raw provider error body, intended for diagnostics only.</summary>
        public string ProviderErrorBody { get; }
    }

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

        /// <summary>Optional per-request allowlist of tool names after orchestrator filtering.</summary>
        public IReadOnlyList<string> AllowedToolNames { get; set; }

        /// <summary>Разрешить/запретить вызов одного и того же инструмента подряд (null = перекладывается на глобальные настройки).</summary>
        public bool? AllowDuplicateToolCalls { get; set; }

        /// <summary>
        /// How the LLM backend should treat tool selection for this request.
        /// <see cref="LlmToolChoiceMode.Auto"/> = provider default (model decides).
        /// Adapters in the Unity layer translate this to <c>ChatOptions.ToolMode</c>
        /// (Microsoft.Extensions.AI) or to the equivalent provider-native field.
        /// </summary>
        public LlmToolChoiceMode ForcedToolMode { get; set; } = LlmToolChoiceMode.Auto;

        /// <summary>
        /// Tool name to require when <see cref="ForcedToolMode"/> is
        /// <see cref="LlmToolChoiceMode.RequireSpecific"/>. Must match an <see cref="ILlmTool.Name"/>
        /// in <see cref="Tools"/>. Ignored for other modes.
        /// </summary>
        public string RequiredToolName { get; set; } = "";
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

        /// <summary>Stable failure category for UI and retry policies.</summary>
        public LlmErrorCode ErrorCode { get; set; } = LlmErrorCode.None;

        /// <summary>HTTP status code when the failure came from an HTTP backend.</summary>
        public int? HttpStatus { get; set; }

        /// <summary>Provider hint for when the request may be retried.</summary>
        public int? RetryAfterSeconds { get; set; }

        /// <summary>Raw provider error body, intended for diagnostics only.</summary>
        public string ProviderErrorBody { get; set; } = "";

        /// <summary>Model identifier used by the provider when known.</summary>
        public string Model { get; set; } = "";

        /// <summary>Заполняется OpenAI-compatible HTTP при наличии <c>usage</c> в JSON.</summary>
        public int? PromptTokens { get; set; }

        /// <summary>Токены completion из usage (HTTP).</summary>
        public int? CompletionTokens { get; set; }

        /// <summary>Суммарные токены из usage (HTTP).</summary>
        public int? TotalTokens { get; set; }
    }

    /// <summary>Один чанк стриминга от LLM: текстовый фрагмент или признак завершения.</summary>
    public sealed class LlmStreamChunk
    {
        /// <summary>Текстовый фрагмент ответа (может быть пустым при завершении).</summary>
        public string Text { get; set; } = "";

        /// <summary>true если это последний чанк и стрим завершён.</summary>
        public bool IsDone { get; set; }

        /// <summary>Ошибка стриминга (если есть).</summary>
        public string Error { get; set; }

        /// <summary>Stable failure category for UI and retry policies.</summary>
        public LlmErrorCode ErrorCode { get; set; } = LlmErrorCode.None;

        /// <summary>HTTP status code when the failure came from an HTTP backend.</summary>
        public int? HttpStatus { get; set; }

        /// <summary>Provider hint for when the request may be retried.</summary>
        public int? RetryAfterSeconds { get; set; }

        /// <summary>Model identifier used by the provider when known.</summary>
        public string Model { get; set; } = "";

        /// <summary>Usage — заполняется только в финальном чанке, если бэкенд отдаёт usage.</summary>
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
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

        /// <summary>
        /// Стриминг ответа модели: возвращает чанки текста по мере генерации.
        /// По умолчанию делает fallback к <see cref="CompleteAsync"/> и отдаёт результат одним чанком.
        /// <para>
        /// ⚠️ <b>ВАЖНО для обёрток/декораторов.</b> Любой <see cref="ILlmClient"/>, который
        /// оборачивает другой клиент (логирование, роутинг по ролям, тайм-ауты, повторы),
        /// <b>обязан явно переопределять</b> этот метод и делегировать его в нижележащий
        /// клиент через <c>await foreach</c>. Иначе будет вызван default-fallback, который
        /// свернёт весь поток в один финальный чанк после окончания генерации — стриминг
        /// в UI окажется «невидимым» и пользователь увидит ответ одномоментно.
        /// </para>
        /// </summary>
#if UNITY_2021_3_OR_NEWER
        virtual async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            LlmCompletionResult result = await CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            if (result == null)
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = "null result",
                    ErrorCode = LlmErrorCode.ProviderError
                };
                yield break;
            }

            if (!result.Ok)
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = result.Error,
                    ErrorCode = result.ErrorCode,
                    HttpStatus = result.HttpStatus,
                    RetryAfterSeconds = result.RetryAfterSeconds,
                    Model = result.Model
                };
                yield break;
            }

            yield return new LlmStreamChunk
            {
                Text = result.Content ?? "",
                IsDone = true,
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                TotalTokens = result.TotalTokens,
                Model = result.Model
            };
        }
#endif
    }
}
