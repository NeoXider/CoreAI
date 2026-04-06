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

        /// <summary>
        /// Универсальный стартовый системный промпт — идёт ПЕРЕД промптом каждого агента.
        /// Задаёт общие правила для всех моделей: формат вывода, ограничения, стиль.
        /// По умолчанию: пустой (отключён). Добавляется в начало системного промпта каждого агента.
        /// </summary>
        /// <example>
        /// CoreAISettings.UniversalSystemPromptPrefix = 
        ///     "You are an AI agent in a Unity game. Always respond in the expected format. " +
        ///     "Never break character. Use tools when appropriate.";
        /// </example>
        public static string UniversalSystemPromptPrefix { get; set; } = "";

        /// <summary>
        /// Общая температура генерации для всех агентов.
        /// 0.0 = детерминировано (максимально предсказуемо), 2.0 = максимально случайно.
        /// По умолчанию: 0.1. Можно переопределить на уровне агента через AgentBuilder.WithTemperature().
        /// </summary>
        public static float Temperature { get; set; } = 0.1f;

        /// <summary>
        /// Максимум итераций tool calling за один запрос (сколько раз модель может вызвать инструменты подряд).
        /// По умолчанию: 2. Можно менять до инициализации.
        /// </summary>
        public static int MaxToolCallIterations { get; set; } = 2;
    }
}