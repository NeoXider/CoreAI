namespace CoreAI
{
    /// <summary>
    /// Глобальные настройки CoreAI. Публичный статический класс для быстрой настройки поведения.
    /// Все значения можно менять до инициализации системы.
    /// </summary>
    public static class CoreAISettings
    {
        /// <summary>
        /// Максимум автоматических повторов Programmer при ошибке Lua.
        /// По умолчанию: 3. Можно менять до инициализации.
        /// </summary>
        public static int MaxLuaRepairGenerations { get; set; } = 3;

        /// <summary>
        /// Максимум повторов при неудачном tool call (модель не распознала формат).
        /// По умолчанию: 3. Можно менять до инициализации.
        /// </summary>
        public static int MaxToolCallRetries { get; set; } = 3;

        /// <summary>
        /// Включить подробное логирование MEAI pipeline.
        /// По умолчанию: false (только ошибки).
        /// </summary>
        public static bool EnableMeaiDebugLogging { get; set; } = false;

        /// <summary>
        /// Таймаут LLM запросов в секундах. 0 = без таймаута.
        /// По умолчанию: 300 (5 минут).
        /// </summary>
        public static int LlmRequestTimeoutSeconds { get; set; } = 300;

        /// <summary>
        /// Логировать сырые HTTP request/response (заголовки, тело, код ответа).
        /// По умолчанию: false.
        /// </summary>
        public static bool EnableHttpDebugLogging { get; set; } = false;

        /// <summary>
        /// Логировать количество токенов (input, output, total) в каждом ответе.
        /// По умолчанию: true.
        /// </summary>
        public static bool LogTokenUsage { get; set; } = true;

        /// <summary>
        /// Логировать время отклика LLM (latency в миллисекундах).
        /// По умолчанию: true.
        /// </summary>
        public static bool LogLlmLatency { get; set; } = true;

        /// <summary>
        /// Логировать ошибки подключения к LLM (timeout, network error).
        /// По умолчанию: true.
        /// </summary>
        public static bool LogLlmConnectionErrors { get; set; } = true;

        /// <summary>
        /// Размер контекстного окна по умолчанию (токены).
        /// По умолчанию: 8192. Можно менять до инициализации.
        /// Синхронизируется из CoreAISettingsAsset при старте.
        /// </summary>
        public static int ContextWindowTokens { get; set; } = 8192;
    }
}