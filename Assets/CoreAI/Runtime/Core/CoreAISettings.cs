namespace CoreAI
{
    /// <summary>
    /// Статический прокси к <see cref="ICoreAISettings"/>: делегирует чтение в DI-зарегистрированный
    /// экземпляр (<see cref="Instance"/>), но позволяет переопределить любое свойство напрямую
    /// через статические сеттеры (полезно для тестов и legacy-кода).
    /// <para/>
    /// <b>Приоритет:</b> локальный override (если был вызван set) → <see cref="Instance"/> → значение по умолчанию.
    /// </summary>
    public static class CoreAISettings
    {
        /// <summary>
        /// DI-зарегистрированный экземпляр настроек.
        /// Устанавливается из <c>CoreAILifetimeScope</c> при старте, либо вручную из теста.
        /// Если null — используются значения по умолчанию.
        /// </summary>
        public static ICoreAISettings Instance { get; set; }

        #region Override storage (nullable — null means "use Instance")

        private static int? _maxLuaRepairRetries;
        private static bool? _enableMeaiDebugLogging;
        private static int? _llmRequestTimeoutSeconds;
        private static int? _maxLlmRequestRetries;
        private static bool? _enableHttpDebugLogging;
        private static bool? _logTokenUsage;
        private static bool? _logLlmLatency;
        private static bool? _logLlmConnectionErrors;
        private static int? _contextWindowTokens;
        private static string _universalSystemPromptPrefix;
        private static bool _universalSystemPromptPrefixSet;
        private static float? _temperature;
        private static int? _maxToolCallRetries;
        private static bool? _logToolCalls;
        private static bool? _logToolCallArguments;
        private static bool? _logToolCallResults;
        private static bool? _logMeaiToolCallingSteps;
        private static bool? _allowDuplicateToolCalls;
        private static bool? _enableStreaming;

        #endregion

        #region Defaults

        private const int DefaultMaxLuaRepairRetries = 3;
        private const bool DefaultEnableMeaiDebugLogging = false;
        private const int DefaultLlmRequestTimeoutSeconds = 300;
        private const int DefaultMaxLlmRequestRetries = 2;
        private const bool DefaultEnableHttpDebugLogging = false;
        private const bool DefaultLogTokenUsage = true;
        private const bool DefaultLogLlmLatency = true;
        private const bool DefaultLogLlmConnectionErrors = true;
        private const int DefaultContextWindowTokens = 8192;
        private const float DefaultTemperature = 0.1f;
        private const int DefaultMaxToolCallRetries = 3;
        private const bool DefaultLogToolCalls = true;
        private const bool DefaultLogToolCallArguments = true;
        private const bool DefaultLogToolCallResults = true;
        private const bool DefaultLogMeaiToolCallingSteps = true;
        private const bool DefaultAllowDuplicateToolCalls = false;
        private const bool DefaultEnableStreaming = true;

        internal const string DefaultUniversalSystemPromptPrefix =
            "CRITICAL RULES FOR ALL AGENTS:\n" +
            "1. TOOL CALLING: When tools/functions are available, you MUST use them (function calling format). NEVER output JSON in your text response if tools are available to do the job.\n" +
            "2. STRICT ADHERENCE: You must follow the user's task or hint EXACTLY. Do not hallucinate, invent, or add creative flair to tool arguments unless strictly requested.\n" +
            "3. NO CHIT-CHAT: Respond concisely. Do not explain what you are doing unless asked.\n" +
            "4. TOOL LIFECYCLE: If a tool returns a success message, continue with the NEXT step of the task. Do not call the same tool again with the same arguments.";

        #endregion

        #region Properties — delegate to Instance, allow override

        /// <summary>
        /// Максимум подряд неудачных Lua repair до прерывания повторов Programmer.
        /// Счётчик увеличивается при каждой ошибке Lua, сбрасывается при успешном выполнении.
        /// По умолчанию: 3.
        /// </summary>
        public static int MaxLuaRepairRetries
        {
            get => _maxLuaRepairRetries ?? Instance?.MaxLuaRepairRetries ?? DefaultMaxLuaRepairRetries;
            set => _maxLuaRepairRetries = value;
        }

        /// <summary>
        /// Включить подробное логирование MEAI pipeline.
        /// По умолчанию: false (только ошибки).
        /// </summary>
        public static bool EnableMeaiDebugLogging
        {
            get => _enableMeaiDebugLogging ?? Instance?.EnableMeaiDebugLogging ?? DefaultEnableMeaiDebugLogging;
            set => _enableMeaiDebugLogging = value;
        }

        /// <summary>
        /// Таймаут LLM запросов в секундах. 0 = без таймаута.
        /// По умолчанию: 300 (5 минут).
        /// </summary>
        public static int LlmRequestTimeoutSeconds
        {
            get => _llmRequestTimeoutSeconds ?? (int?)(Instance?.LlmRequestTimeoutSeconds) ?? DefaultLlmRequestTimeoutSeconds;
            set => _llmRequestTimeoutSeconds = value;
        }

        /// <summary>
        /// Максимальное количество попыток запроса к LLM при таймаутах или сетевых ошибках.
        /// По умолчанию: 2.
        /// </summary>
        public static int MaxLlmRequestRetries
        {
            get => _maxLlmRequestRetries ?? Instance?.MaxLlmRequestRetries ?? DefaultMaxLlmRequestRetries;
            set => _maxLlmRequestRetries = value;
        }

        /// <summary>
        /// Логировать сырые HTTP request/response (заголовки, тело, код ответа).
        /// По умолчанию: false.
        /// </summary>
        public static bool EnableHttpDebugLogging
        {
            get => _enableHttpDebugLogging ?? Instance?.EnableHttpDebugLogging ?? DefaultEnableHttpDebugLogging;
            set => _enableHttpDebugLogging = value;
        }

        /// <summary>
        /// Логировать количество токенов (input, output, total) в каждом ответе.
        /// По умолчанию: true.
        /// </summary>
        public static bool LogTokenUsage
        {
            get => _logTokenUsage ?? Instance?.LogTokenUsage ?? DefaultLogTokenUsage;
            set => _logTokenUsage = value;
        }

        /// <summary>
        /// Логировать время отклика LLM (latency в миллисекундах).
        /// По умолчанию: true.
        /// </summary>
        public static bool LogLlmLatency
        {
            get => _logLlmLatency ?? Instance?.LogLlmLatency ?? DefaultLogLlmLatency;
            set => _logLlmLatency = value;
        }

        /// <summary>
        /// Логировать ошибки подключения к LLM (timeout, network error).
        /// По умолчанию: true.
        /// </summary>
        public static bool LogLlmConnectionErrors
        {
            get => _logLlmConnectionErrors ?? Instance?.LogLlmConnectionErrors ?? DefaultLogLlmConnectionErrors;
            set => _logLlmConnectionErrors = value;
        }

        /// <summary>
        /// Размер контекстного окна по умолчанию (токены).
        /// По умолчанию: 8192. Синхронизируется из CoreAISettingsAsset при старте.
        /// </summary>
        public static int ContextWindowTokens
        {
            get => _contextWindowTokens ?? Instance?.ContextWindowTokens ?? DefaultContextWindowTokens;
            set => _contextWindowTokens = value;
        }

        /// <summary>
        /// Универсальный стартовый системный промпт — идёт ПЕРЕД промптом каждого агента.
        /// Задаёт общие правила для всех моделей: формат вывода, ограничения, стиль.
        /// </summary>
        /// <example>
        /// CoreAISettings.UniversalSystemPromptPrefix =
        ///     "You are an AI agent in a Unity game. Always respond in the expected format. " +
        ///     "Never break character. Use tools when appropriate.";
        /// </example>
        public static string UniversalSystemPromptPrefix
        {
            get => _universalSystemPromptPrefixSet
                ? _universalSystemPromptPrefix
                : Instance?.UniversalSystemPromptPrefix ?? DefaultUniversalSystemPromptPrefix;
            set
            {
                _universalSystemPromptPrefix = value;
                _universalSystemPromptPrefixSet = true;
            }
        }

        /// <summary>
        /// Общая температура генерации для всех агентов.
        /// 0.0 = детерминировано, 2.0 = максимально случайно. По умолчанию: 0.1.
        /// </summary>
        public static float Temperature
        {
            get => _temperature ?? Instance?.Temperature ?? DefaultTemperature;
            set => _temperature = value;
        }

        /// <summary>
        /// Максимум подряд неудачных tool call до прерывания агента.
        /// Счётчик сбрасывается при каждом успешном вызове инструмента. По умолчанию: 3.
        /// </summary>
        public static int MaxToolCallRetries
        {
            get => _maxToolCallRetries ?? Instance?.MaxToolCallRetries ?? DefaultMaxToolCallRetries;
            set => _maxToolCallRetries = value;
        }

        /// <summary>
        /// Логировать вызовы инструментов (название, успех/неудача).
        /// По умолчанию: true.
        /// </summary>
        public static bool LogToolCalls
        {
            get => _logToolCalls ?? Instance?.LogToolCalls ?? DefaultLogToolCalls;
            set => _logToolCalls = value;
        }

        /// <summary>
        /// Логировать аргументы tool call.
        /// По умолчанию: true.
        /// </summary>
        public static bool LogToolCallArguments
        {
            get => _logToolCallArguments ?? Instance?.LogToolCallArguments ?? DefaultLogToolCallArguments;
            set => _logToolCallArguments = value;
        }

        /// <summary>
        /// Логировать результаты tool call.
        /// По умолчанию: true.
        /// </summary>
        public static bool LogToolCallResults
        {
            get => _logToolCallResults ?? Instance?.LogToolCallResults ?? DefaultLogToolCallResults;
            set => _logToolCallResults = value;
        }

        /// <summary>
        /// Логировать шаги MEAI FunctionInvokingChatClient.
        /// По умолчанию: true.
        /// </summary>
        public static bool LogMeaiToolCallingSteps
        {
            get => _logMeaiToolCallingSteps ?? Instance?.LogMeaiToolCallingSteps ?? DefaultLogMeaiToolCallingSteps;
            set => _logMeaiToolCallingSteps = value;
        }

        /// <summary>
        /// Разрешить агенту вызывать один и тот же инструмент с теми же аргументами за одну сессию.
        /// По умолчанию: false (чтобы защитить маленькие модели от зацикливания).
        /// </summary>
        public static bool AllowDuplicateToolCalls
        {
            get => _allowDuplicateToolCalls ?? Instance?.AllowDuplicateToolCalls ?? DefaultAllowDuplicateToolCalls;
            set => _allowDuplicateToolCalls = value;
        }

        /// <summary>
        /// Глобальное включение стриминга ответов LLM. Может быть переопределено
        /// на уровне роли через <see cref="AgentMemoryPolicy"/> или на уровне UI
        /// через <c>CoreAiChatConfig.EnableStreaming</c>.
        /// По умолчанию: true.
        /// </summary>
        public static bool EnableStreaming
        {
            get => _enableStreaming ?? Instance?.EnableStreaming ?? DefaultEnableStreaming;
            set => _enableStreaming = value;
        }

        #endregion

        /// <summary>
        /// Сбросить все локальные переопределения. После вызова все свойства будут
        /// делегироваться в <see cref="Instance"/> (или использовать значения по умолчанию).
        /// Полезно для очистки состояния между тестами.
        /// </summary>
        public static void ResetOverrides()
        {
            _maxLuaRepairRetries = null;
            _enableMeaiDebugLogging = null;
            _llmRequestTimeoutSeconds = null;
            _maxLlmRequestRetries = null;
            _enableHttpDebugLogging = null;
            _logTokenUsage = null;
            _logLlmLatency = null;
            _logLlmConnectionErrors = null;
            _contextWindowTokens = null;
            _universalSystemPromptPrefix = null;
            _universalSystemPromptPrefixSet = false;
            _temperature = null;
            _maxToolCallRetries = null;
            _logToolCalls = null;
            _logToolCallArguments = null;
            _logToolCallResults = null;
            _logMeaiToolCallingSteps = null;
            _allowDuplicateToolCalls = null;
            _enableStreaming = null;
        }
    }
}