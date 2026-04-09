using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Тип LLM-бэкенда для быстрого переключения в одном месте.
    /// </summary>
    public enum LlmBackendType
    {
        /// <summary>Автоматический выбор: LLMUnity → HTTP API → Offline.</summary>
        Auto = 0,

        /// <summary>Локальная модель через LLMUnity (GGUF на сцене).</summary>
        LlmUnity = 1,

        /// <summary>HTTP API — OpenAI-compatible (LM Studio, OpenRouter, Qwen API и т.д.).</summary>
        OpenAiHttp = 2,

        /// <summary>Офлайн режим — без подключений к LLM, детерминированные ответы для тестов.</summary>
        Offline = 3
    }

    /// <summary>
    /// Приоритет бэкендов в Auto режиме.
    /// </summary>
    public enum LlmAutoPriority
    {
        /// <summary>Сначала LLMUnity, затем HTTP API, затем Offline.</summary>
        LlmUnityFirst = 0,

        /// <summary>Сначала HTTP API, затем LLMUnity, затем Offline.</summary>
        HttpFirst = 1
    }

    /// <summary>
    /// Единые настройки CoreAI — всё в одном ScriptableObject.
    /// Создаётся через: <c>Create → CoreAI → CoreAI Settings</c>
    /// 
    /// Автоматически подхватывается как синглтон через <see cref="Instance"/>.
    /// Используется по всему проекту для получения API-ключа, URL, модели и других настроек.
    /// </summary>
    [CreateAssetMenu(menuName = "CoreAI/CoreAI Settings", fileName = "CoreAISettings")]
    public sealed class CoreAISettingsAsset : ScriptableObject, ICoreAISettings
    {
        #region Singleton

        private static CoreAISettingsAsset _instance;

        /// <summary>
        /// Глобальный экземпляр настроек. Автоматически ищется в Resources при первом обращении.
        /// Также назначается через <see cref="SetInstance"/> из LifetimeScope.
        /// </summary>
        public static CoreAISettingsAsset Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<CoreAISettingsAsset>("CoreAISettings");
                }

                return _instance;
            }
        }

        /// <summary>Установить экземпляр вручную (вызывается из LifetimeScope).</summary>
        public static void SetInstance(CoreAISettingsAsset settings)
        {
            _instance = settings;
        }

        /// <summary>Сбросить экземпляр (для тестов).</summary>
        public static void ResetInstance()
        {
            _instance = null;
        }

        #endregion

        #region Основные настройки LLM

        [Header("🤖 LLM Backend")]
        [Tooltip("Какой бэкенд использовать: Auto, LLMUnity (локально), HTTP API или без LLM.")]
        [SerializeField]
        private LlmBackendType backendType = LlmBackendType.Auto;

        [Tooltip("Приоритет в Auto режиме: LLMUnity сначала или HTTP API сначала.")] [SerializeField]
        private LlmAutoPriority autoPriority = LlmAutoPriority.LlmUnityFirst;

        [Header("🌐 HTTP API (OpenAI-compatible)")]
        [Tooltip(
            "База URL без завершающего слэша: https://api.openai.com/v1, http://localhost:1234/v1 (LM Studio), http://localhost:5001/v1 (Qwen) и т.д.")]
        [SerializeField]
        private string apiBaseUrl = "http://localhost:1234/v1";

        [Tooltip("API ключ (Bearer токен). Для OpenAI: sk-..., для LM Studio: пусто, для Qwen: ваш ключ.")]
        [SerializeField]
        private string apiKey = "";

        [Tooltip("Название модели: gpt-4o-mini, qwen3.5-4b, llama-3-8b и т.д.")] [SerializeField]
        private string modelName = "gpt-4o-mini";

        [Tooltip("Температура генерации: 0.0 — детерминировано, 2.0 — максимально случайно. Общая для всех агентов.")]
        [SerializeField]
        [Range(0f, 2f)]
        private float temperature = 0.1f;

        [Tooltip("Максимум токенов в ответе. 0 = без лимита.")] [SerializeField]
        private int maxTokens = 4096;

        [Tooltip("Таймаут HTTP-запроса в секундах. 0 = без таймаута.")] [SerializeField] [Min(0)]
        private int requestTimeoutSeconds = 120;

        [Header("💾 LLMUnity (локальная модель)")]
        [Tooltip("Имя GameObject с LLMAgent на сцене. Пусто = найти автоматически.")]
        [SerializeField]
        private string llmUnityAgentName = "";

        [Tooltip("Путь к .gguf модели. Пусто = автоопределение через Model Manager.")] [SerializeField]
        private string ggufModelPath = "Qwen3.5-2B-Q4_K_M.gguf";

        [Tooltip("Не уничтожать LLMUnity GameObject при смене сцены.")] [SerializeField]
        private bool llmUnityDontDestroyOnLoad = true;

        [Tooltip("Таймаут запуска LLMUnity сервиса (секунды).")] [SerializeField] [Min(5f)]
        private float llmUnityStartupTimeoutSeconds = 120f;

        [Tooltip("Задержка после запуска LLMUnity сервиса (секунды).")] [SerializeField] [Min(0f)]
        private float llmUnityStartupDelaySeconds = 1f;

        [Tooltip("Не останавливать LLMUnity сервер между запросами (keep-alive). Ускоряет тесты.")] [SerializeField]
        private bool llmUnityKeepAlive = false;

        [Tooltip(
            "Включить режим размышлений (thinking/reasoning). Поддерживается Qwen3.5, DeepSeek и др. Работает как для API так и для LLMUnity.")]
        [SerializeField]
        private bool enableReasoning = false;

        [Tooltip("Максимум параллельных чатов с LLMUnity. 1 = последовательно.")] [SerializeField] [Min(1)]
        private int llmUnityMaxConcurrentChats = 1;

        [Tooltip("Количество слоев для выгрузки на GPU. 0 = CPU, 99 = все слои (как LM Studio).")]
        [SerializeField]
        [Min(0)]
        private int llmUnityNumGPULayers = 99;

        [Header("⚙️ Общие настройки")]
        [Tooltip(
            "Универсальный стартовый промпт — идёт ПЕРЕД промптом каждого агента. Задаёт общие правила для всех моделей.")]
        [TextArea(3, 6)]
        [SerializeField]
        private string universalSystemPromptPrefix = "";

        [Tooltip(
            "Максимум подряд неудачных Lua repair до прерывания повторов Programmer. Счётчик сбрасывается при успехе.")]
        [SerializeField]
        [Min(1)]
        private int maxLuaRepairRetries = 3;

        [Tooltip("Максимум подряд неудачных tool call до прерывания агента. Счётчик сбрасывается при успехе.")]
        [SerializeField]
        [Min(1)]
        private int maxToolCallRetries = 3;

        [Tooltip("Максимальное количество попыток запроса к LLM при таймаутах или сетевых ошибках.")]
        [SerializeField]
        [Min(1)]
        private int maxLlmRequestRetries = 2;

        [Tooltip("Контекстное окно по умолчанию (токены).")] [SerializeField] [Min(256)]
        private int contextWindowTokens = 8192;

        [Header("🔌 Offline режим (без LLM)")]
        [Tooltip("Возвращать кастомный текст вместо заглушки по ролям.")]
        [SerializeField]
        private bool offlineUseCustomResponse = false;

        [Tooltip("Текст который будет возвращаться вместо всех запросов.")] [SerializeField] [TextArea(3, 8)]
        private string offlineCustomResponse = "Offline mode: LLM unavailable";

        [Tooltip("Для каких ролей использовать кастомный ответ (* = все).")] [SerializeField]
        private string offlineCustomResponseRoles = "*";

        [Header("🔧 Отладка")] [Tooltip("Включить подробное логирование MEAI pipeline.")] [SerializeField]
        private bool enableMeaiDebugLogging = false;

        [Tooltip("Логировать сырые HTTP request/response (для отладки API).")] [SerializeField]
        private bool enableHttpDebugLogging = false;

        [Tooltip("Логировать входящие промпты (system, user) и инструменты.")] [SerializeField]
        private bool logLlmInput = true;

        [Tooltip("Логировать исходящие ответы модели и результаты tool calls.")] [SerializeField]
        private bool logLlmOutput = true;

        [Tooltip("Логировать количество токенов (input, output, total) в каждом ответе.")] [SerializeField]
        private bool logTokenUsage = true;

        [Tooltip("Логировать время отклика LLM (latency в миллисекундах).")] [SerializeField]
        private bool logLlmLatency = true;

        [Tooltip("Логировать ошибки подключения к LLM (timeout, network error).")] [SerializeField]
        private bool logLlmConnectionErrors = true;

        [Header("🔨 Tool Call Logging")]
        [Tooltip("Логировать каждый вызов инструмента (название, аргументы, успех/неудача).")]
        [SerializeField]
        private bool logToolCalls = true;

        [Tooltip("Логировать аргументы tool call (может быть многословно).")] [SerializeField]
        private bool logToolCallArguments = true;

        [Tooltip("Логировать результаты tool call (ответы инструментов).")] [SerializeField]
        private bool logToolCallResults = true;

        [Tooltip("Логировать внутренние шаги MEAI FunctionInvokingChatClient (итерации, retry).")] [SerializeField]
        private bool logMeaiToolCallingSteps = true;

        [Tooltip("Таймаут LLM-запроса в LifetimeScope (секунды). 0 = без ограничения.")] [SerializeField] [Min(0f)]
        private float llmRequestTimeoutSeconds = 15f;

        [Tooltip("Максимум параллельных задач оркестратора.")] [SerializeField] [Min(1)]
        private int maxConcurrentOrchestrations = 2;

        [Tooltip("Логировать метрики оркестратора.")] [SerializeField]
        private bool logOrchestrationMetrics = false;

        #endregion

        #region Properties

        /// <summary>Тип текущего бэкенда.</summary>
        public LlmBackendType BackendType => backendType;

        /// <summary>Используется ли HTTP API.</summary>
        public bool UseHttpApi => backendType == LlmBackendType.OpenAiHttp;

        /// <summary>Используется ли LLMUnity.</summary>
        public bool UseLlmUnity => backendType == LlmBackendType.LlmUnity || backendType == LlmBackendType.Auto;

        /// <summary>Используется ли офлайн режим (без LLM).</summary>
        public bool UseOffline => backendType == LlmBackendType.Offline;

        /// <summary>Приоритет бэкендов в Auto режиме.</summary>
        public LlmAutoPriority AutoPriority => autoPriority;

        // Offline
        /// <summary>Использовать кастомный ответ вместо заглушки по ролям.</summary>
        public bool OfflineUseCustomResponse => offlineUseCustomResponse;

        /// <summary>Кастомный текст для Offline режима.</summary>
        public string OfflineCustomResponse => string.IsNullOrWhiteSpace(offlineCustomResponse)
            ? "Offline mode: LLM unavailable"
            : offlineCustomResponse;

        /// <summary>Для каких ролей использовать кастомный ответ (* = все).</summary>
        public string OfflineCustomResponseRoles =>
            string.IsNullOrWhiteSpace(offlineCustomResponseRoles) ? "*" : offlineCustomResponseRoles;

        /// <summary>Нужно ли использовать кастомный ответ для данной роли.</summary>
        public bool ShouldUseOfflineCustomResponse(string roleId)
        {
            if (!offlineUseCustomResponse)
            {
                return false;
            }

            if (offlineCustomResponseRoles == "*")
            {
                return true;
            }

            if (string.IsNullOrEmpty(roleId))
            {
                return false;
            }

            string[] roles = offlineCustomResponseRoles.Split(',');
            foreach (string r in roles)
            {
                if (r.Trim().Equals(roleId, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // HTTP API
        /// <summary>Базовый URL API без завершающего слэша.</summary>
        public string ApiBaseUrl =>
            string.IsNullOrWhiteSpace(apiBaseUrl) ? "http://localhost:1234/v1" : apiBaseUrl.TrimEnd('/');

        /// <summary>API ключ (Bearer токен).</summary>
        public string ApiKey => apiKey ?? "";

        /// <summary>Название модели.</summary>
        public string ModelName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(modelName))
                {
                    return modelName;
                }

                // Для LLMUnity возвращаем имя GGUF файла
                if (backendType == LlmBackendType.LlmUnity || backendType == LlmBackendType.Auto)
                {
                    if (!string.IsNullOrWhiteSpace(ggufModelPath))
                    {
                        return ggufModelPath;
                    }
                }

                return "gpt-4o-mini";
            }
        }

        /// <summary>Температура генерации.</summary>
        public float Temperature => temperature;

        /// <summary>Максимум токенов в ответе.</summary>
        public int MaxTokens => maxTokens;

        /// <summary>Таймаут HTTP-запроса.</summary>
        public int RequestTimeoutSeconds => requestTimeoutSeconds <= 0 ? 120 : requestTimeoutSeconds;

        // LLMUnity
        /// <summary>Имя GameObject с LLMAgent.</summary>
        public string LlmUnityAgentName => llmUnityAgentName;

        /// <summary>Путь к .gguf модели.</summary>
        public string GgufModelPath => ggufModelPath ?? "";

        /// <summary>Не уничтожать при смене сцены.</summary>
        public bool LlmUnityDontDestroyOnLoad => llmUnityDontDestroyOnLoad;

        /// <summary>Таймаут запуска LLMUnity (сек).</summary>
        public float LlmUnityStartupTimeoutSeconds =>
            llmUnityStartupTimeoutSeconds < 5f ? 120f : llmUnityStartupTimeoutSeconds;

        /// <summary>Задержка после запуска LLMUnity (сек).</summary>
        public float LlmUnityStartupDelaySeconds => llmUnityStartupDelaySeconds;

        /// <summary>Не останавливать сервер между запросами.</summary>
        public bool LlmUnityKeepAlive => llmUnityKeepAlive;

        /// <summary>Включить режим размышлений (thinking/reasoning). Для Qwen3.5, DeepSeek и др.</summary>
        public bool EnableReasoning => enableReasoning;

        /// <summary>Максимум параллельных чатов.</summary>
        public int LlmUnityMaxConcurrentChats => llmUnityMaxConcurrentChats < 1 ? 1 : llmUnityMaxConcurrentChats;

        /// <summary>Слоев выгружено на GPU (0 = CPU).</summary>
        public int NumGPULayers => llmUnityNumGPULayers < 0 ? 0 : llmUnityNumGPULayers;

        // Общие
        /// <summary>Универсальный стартовый промпт для всех агентов.</summary>
        public string UniversalSystemPromptPrefix => universalSystemPromptPrefix ?? "";

        /// <summary>Максимум подряд неудачных Lua repair попыток.</summary>
        public int MaxLuaRepairRetries => maxLuaRepairRetries < 1 ? 3 : maxLuaRepairRetries;

        /// <summary>Максимум подряд неудачных too call до прерывания агента.</summary>
        public int MaxToolCallRetries => maxToolCallRetries < 1 ? 3 : maxToolCallRetries;

        /// <summary>Максимальное количество попыток запроса к LLM при таймаутах или сетевых ошибках.</summary>
        public int MaxLlmRequestRetries => maxLlmRequestRetries < 1 ? 2 : maxLlmRequestRetries;

        /// <summary>Контекстное окно.</summary>
        public int ContextWindowTokens => contextWindowTokens < 256 ? 8192 : contextWindowTokens;

        // Отладка
        /// <summary>MEAI debug logging.</summary>
        public bool EnableMeaiDebugLogging => enableMeaiDebugLogging;

        /// <summary>HTTP debug logging.</summary>
        public bool EnableHttpDebugLogging => enableHttpDebugLogging;

        /// <summary>Таймаут LLM-запроса.</summary>
        public float LlmRequestTimeoutSeconds => llmRequestTimeoutSeconds;

        /// <summary>Максимум параллельных оркестраций.</summary>
        public int MaxConcurrentOrchestrations => maxConcurrentOrchestrations < 1 ? 2 : maxConcurrentOrchestrations;

        /// <summary>Логирование метрик.</summary>
        public bool LogOrchestrationMetrics => logOrchestrationMetrics;

        /// <summary>Логировать входящие промпты и инструменты.</summary>
        public bool LogLlmInput => logLlmInput;

        /// <summary>Логировать исходящие ответы модели и tool calls.</summary>
        public bool LogLlmOutput => logLlmOutput;

        /// <summary>Логировать количество токенов.</summary>
        public bool LogTokenUsage => logTokenUsage;

        /// <summary>Логировать время отклика LLM.</summary>
        public bool LogLlmLatency => logLlmLatency;

        /// <summary>Логировать ошибки подключения.</summary>
        public bool LogLlmConnectionErrors => logLlmConnectionErrors;

        /// <summary>Логировать вызовы инструментов (название, успех/неудача).</summary>
        public bool LogToolCalls => logToolCalls;

        /// <summary>Логировать аргументы tool call.</summary>
        public bool LogToolCallArguments => logToolCallArguments;

        /// <summary>Логировать результаты tool call.</summary>
        public bool LogToolCallResults => logToolCallResults;

        /// <summary>Логировать шаги MEAI FunctionInvokingChatClient.</summary>
        public bool LogMeaiToolCallingSteps => logMeaiToolCallingSteps;

        #endregion

        #region Runtime Configuration

        /// <summary>
        /// Настроить HTTP API программно (для тестов или динамической конфигурации).
        /// </summary>
        public void ConfigureHttpApi(
            string baseUrl,
            string key,
            string model,
            float temperature = 0.2f,
            int timeoutSeconds = 120,
            int maxTokens = 4096)
        {
            backendType = LlmBackendType.OpenAiHttp;
            apiBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:1234/v1" : baseUrl;
            apiKey = key ?? "";
            modelName = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
            this.temperature = Mathf.Clamp(temperature, 0f, 2f);
            requestTimeoutSeconds = timeoutSeconds <= 0 ? 120 : timeoutSeconds;
            this.maxTokens = maxTokens <= 0 ? 4096 : maxTokens;
        }

        /// <summary>
        /// Переключить на LLMUnity.
        /// </summary>
        public void ConfigureLlmUnity(
            string agentName = "",
            string ggufPath = "Qwen3.5-2B-Q4_K_M.gguf",
            bool keepAlive = false,
            float startupTimeout = 120f,
            float startupDelay = 1f,
            bool dontDestroyOnLoad = true,
            int numGpuLayers = 99)
        {
            backendType = LlmBackendType.LlmUnity;
            llmUnityAgentName = agentName ?? "";
            ggufModelPath = string.IsNullOrWhiteSpace(ggufPath) ? "Qwen3.5-2B-Q4_K_M.gguf" : ggufPath;
            llmUnityKeepAlive = keepAlive;
            llmUnityStartupTimeoutSeconds = startupTimeout < 5f ? 120f : startupTimeout;
            llmUnityStartupDelaySeconds = startupDelay;
            llmUnityDontDestroyOnLoad = dontDestroyOnLoad;
            llmUnityNumGPULayers = numGpuLayers < 0 ? 0 : numGpuLayers;
        }

        /// <summary>
        /// Переключить в офлайн режим (без LLM, детерминированные ответы для тестов).
        /// </summary>
        public void ConfigureOffline()
        {
            backendType = LlmBackendType.Offline;
        }

        /// <summary>
        /// Переключить в автоматический режим (LLMUnity → HTTP API → Offline).
        /// </summary>
        public void ConfigureAuto()
        {
            backendType = LlmBackendType.Auto;
        }

        /// <summary>
        /// Синхронизировать статические CoreAISettings с значениями этого ScriptableObject.
        /// Вызывается при старте из CoreAILifetimeScope.
        /// </summary>
        public void SyncToStaticSettings()
        {
            CoreAISettings.MaxLuaRepairRetries = MaxLuaRepairRetries;
            CoreAISettings.MaxToolCallRetries = MaxToolCallRetries;
            CoreAISettings.MaxLlmRequestRetries = MaxLlmRequestRetries;
            CoreAISettings.EnableMeaiDebugLogging = EnableMeaiDebugLogging;
            CoreAISettings.LlmRequestTimeoutSeconds = (int)LlmRequestTimeoutSeconds;
            CoreAISettings.EnableHttpDebugLogging = EnableHttpDebugLogging;
            CoreAISettings.LogTokenUsage = LogTokenUsage;
            CoreAISettings.LogLlmLatency = LogLlmLatency;
            CoreAISettings.LogLlmConnectionErrors = LogLlmConnectionErrors;
            CoreAISettings.ContextWindowTokens = ContextWindowTokens;
            CoreAISettings.UniversalSystemPromptPrefix = UniversalSystemPromptPrefix;
            CoreAISettings.Temperature = Temperature;
            CoreAISettings.LogToolCalls = LogToolCalls;
            CoreAISettings.LogToolCallArguments = LogToolCallArguments;
            CoreAISettings.LogToolCallResults = LogToolCallResults;
            CoreAISettings.LogMeaiToolCallingSteps = LogMeaiToolCallingSteps;
        }

        #endregion

        #region Unity Editor Helpers

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Валидация при изменении в Inspector
            if (requestTimeoutSeconds < 0)
            {
                requestTimeoutSeconds = 120;
            }

            if (maxLuaRepairRetries < 1)
            {
                maxLuaRepairRetries = 3;
            }

            if (maxToolCallRetries < 1)
            {
                maxToolCallRetries = 3;
            }

            if (maxLlmRequestRetries < 1)
            {
                maxLlmRequestRetries = 2;
            }

            if (contextWindowTokens < 256)
            {
                contextWindowTokens = 8192;
            }

            if (maxConcurrentOrchestrations < 1)
            {
                maxConcurrentOrchestrations = 2;
            }

            if (llmRequestTimeoutSeconds < 0f)
            {
                llmRequestTimeoutSeconds = 15f;
            }

            if (llmUnityStartupTimeoutSeconds < 5f)
            {
                llmUnityStartupTimeoutSeconds = 120f;
            }

            if (llmUnityStartupDelaySeconds < 0f)
            {
                llmUnityStartupDelaySeconds = 1f;
            }

            if (llmUnityMaxConcurrentChats < 1)
            {
                llmUnityMaxConcurrentChats = 1;
            }

            if (llmUnityNumGPULayers < 0)
            {
                llmUnityNumGPULayers = 0;
            }
        }
#endif

        #endregion
    }
}