using System.Collections.Generic;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Абстракция настроек HTTP API (OpenAI-compatible).
    /// Позволяет использовать разные источники настроек в хосте (Unity asset, server config и т.д.).
    /// </summary>
    public interface IOpenAiHttpSettings
    {
        /// <summary>Базовый URL API без завершающего слэша.</summary>
        string ApiBaseUrl { get; }

        /// <summary>Bearer-токен (API ключ).</summary>
        string ApiKey { get; }

        /// <summary>
        /// Full Authorization header value. When empty, clients fall back to <see cref="ApiKey"/> as a bearer token.
        /// </summary>
        string AuthorizationHeader { get; }

        /// <summary>Название модели.</summary>
        string Model { get; }

        /// <summary>Температура генерации (0.0–2.0).</summary>
        float Temperature { get; }

        /// <summary>Таймаут HTTP-запроса в секундах.</summary>
        int RequestTimeoutSeconds { get; }

        /// <summary>Максимум токенов в ответе.</summary>
        int MaxTokens { get; }

        /// <summary>Логировать входящие промпты (system, user) и инструменты.</summary>
        bool LogLlmInput { get; }

        /// <summary>Логировать исходящие ответы модели и результаты tool calls.</summary>
        bool LogLlmOutput { get; }

        /// <summary>Логировать сырые HTTP request/response JSON.</summary>
        bool EnableHttpDebugLogging { get; }

        /// <summary>
        /// Provides additional HTTP headers sent with every request (e.g., tenant-id, request-id, idempotency-key).
        /// Returning null or empty falls back to no extra headers.
        /// </summary>
        IRequestHeaderProvider? HeaderProvider { get; }
    }

    /// <summary>
    /// Supplies custom HTTP headers for OpenAI-compatible requests.
    /// Implementations can be transient (new values per request) or static.
    /// </summary>
    public interface IRequestHeaderProvider
    {
        /// <summary>
        /// Returns headers to add to the outgoing request.
        /// Called once per logical request (retries reuse the same headers via <see cref="IdempotencyKey"/>).
        /// </summary>
        IReadOnlyList<KeyValuePair<string, string>> GetHeaders();

        /// <summary>
        /// A stable idempotency key for this logical request (reused across retries).
        /// If null/empty, a new key is generated automatically.
        /// </summary>
        string IdempotencyKey { get; }

        /// <summary>
        /// Unique identifier for this logical request (trace/diagnostic).
        /// Mapped to X-Request-Id.
        /// </summary>
        string RequestId { get; }
    }
}
