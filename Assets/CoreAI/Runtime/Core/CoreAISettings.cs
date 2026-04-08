namespace CoreAI
{
    /// <summary>
    /// Глобальные настройки CoreAI. Публичный статический класс для быстрой настройки поведения.
    /// Все значения можно менять до инициализации системы.
    /// </summary>
    public static class CoreAISettings
    {
        /// <summary>
        /// Максимум подряд неудачных Lua repair до прерывания повторов Programmer.
        /// Счётчик увеличивается при каждой ошибке Lua, сбрасывается при успешном выполнении.
        /// По умолчанию: 3.
        /// </summary>
        public static int MaxLuaRepairRetries { get; set; } = 3;



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
        /// По умолчанию: содержит инструкцию по tool calling для всех агентов. Добавляется в начало системного промпта каждого агента.
        /// </summary>
        /// <example>
        /// CoreAISettings.UniversalSystemPromptPrefix =
        ///     "You are an AI agent in a Unity game. Always respond in the expected format. " +
        ///     "Never break character. Use tools when appropriate.";
        /// </example>
        public static string UniversalSystemPromptPrefix { get; set; } =
            "TOOL CALLING RULES: When tools/functions are provided in the request, you MUST use tool calls (function calling format) to invoke them. " +
            "NEVER output JSON or structured data in your response text when tools are available - always call the appropriate tool instead. " +
            "Only output plain text or JSON in your response when NO tools are provided. " +
            "If a tool call succeeds, continue with your task. If it fails, analyze the error and retry with corrected parameters.";

        /// <summary>
        /// Общая температура генерации для всех агентов.
        /// 0.0 = детерминировано (максимально предсказуемо), 2.0 = максимально случайно.
        /// По умолчанию: 0.1. Можно переопределить на уровне агента через AgentBuilder.WithTemperature().
        /// </summary>
        public static float Temperature { get; set; } = 0.1f;

        /// <summary>
        /// Максимум подряд неудачных tool call до прерывания агента.
        /// Счётчик сбрасывается при каждом успешном вызове инструмента.
        /// По умолчанию: 3.
        /// </summary>
        public static int MaxToolCallRetries { get; set; } = 3;

        /// <summary>
        /// Логировать вызовы инструментов (название, успех/неудача).
        /// По умолчанию: true.
        /// </summary>
        public static bool LogToolCalls { get; set; } = true;

        /// <summary>
        /// Логировать аргументы tool call.
        /// По умолчанию: true.
        /// </summary>
        public static bool LogToolCallArguments { get; set; } = true;

        /// <summary>
        /// Логировать результаты tool call.
        /// По умолчанию: true.
        /// </summary>
        public static bool LogToolCallResults { get; set; } = true;

        /// <summary>
        /// Логировать шаги MEAI FunctionInvokingChatClient.
        /// По умолчанию: true.
        /// </summary>
        public static bool LogMeaiToolCallingSteps { get; set; } = true;
    }
}