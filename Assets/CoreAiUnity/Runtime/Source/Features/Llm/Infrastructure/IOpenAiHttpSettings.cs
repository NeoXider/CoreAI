namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Абстракция настроек HTTP API (OpenAI-compatible).
    /// Позволяет использовать как <see cref="OpenAiHttpLlmSettings"/>, так и <see cref="CoreAISettingsAsset"/>.
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

        // === Отладка ===

        /// <summary>Логировать входящие промпты (system, user) и инструменты.</summary>
        bool LogLlmInput { get; }

        /// <summary>Логировать исходящие ответы модели и результаты tool calls.</summary>
        bool LogLlmOutput { get; }

        /// <summary>Логировать сырые HTTP request/response JSON.</summary>
        bool EnableHttpDebugLogging { get; }
    }
}