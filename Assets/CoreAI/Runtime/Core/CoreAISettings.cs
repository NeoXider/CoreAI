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
    }
}
