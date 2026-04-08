using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Включение OpenAI-совместимого HTTP API вместо локального LLMUnity (выбор в CoreAILifetimeScope на сцене).
    /// Ключ не коммитьте: для билда используйте локальный asset вне репозитория или загрузку из защищённого хранилища.
    /// </summary>
    [CreateAssetMenu(menuName = "CoreAI/LLM/OpenAI-compatible HTTP", fileName = "OpenAiHttpLlmSettings")]
    public sealed class OpenAiHttpLlmSettings : ScriptableObject, IOpenAiHttpSettings
    {
        [Tooltip("Если включено, ILlmClient ходит в HTTP chat/completions вместо LLMAgent на сцене.")] [SerializeField]
        private bool useOpenAiCompatibleHttp;

        [Tooltip(
            "База без завершающего слэша, например https://api.openai.com/v1 или http://localhost:1234/v1 (LM Studio).")]
        [SerializeField]
        private string apiBaseUrl = "https://api.openai.com/v1";

        [SerializeField] private string apiKey = "";

        [SerializeField] private string model = "gpt-4o-mini";

        [SerializeField] [Range(0f, 2f)] private float temperature = 0.2f;

        [SerializeField] [Min(5)] private int requestTimeoutSeconds = 120;

        [SerializeField] [Min(64)] private int maxTokens = 4096;

        [Header("🔧 Отладка")] [Tooltip("Логировать входящие промпты (system, user) и инструменты.")] [SerializeField]
        private bool logLlmInput = true;

        [Tooltip("Логировать исходящие ответы модели и результаты tool calls.")] [SerializeField]
        private bool logLlmOutput = true;

        [Tooltip("Логировать сырые HTTP request/response JSON.")] [SerializeField]
        private bool enableHttpDebugLogging = false;

        /// <summary>Использовать HTTP вместо LLMUnity для клиента, собранного из этого asset.</summary>
        public bool UseOpenAiCompatibleHttp => useOpenAiCompatibleHttp;

        /// <summary>Базовый URL API без завершающего слэша.</summary>
        public string ApiBaseUrl =>
            string.IsNullOrWhiteSpace(apiBaseUrl) ? "https://api.openai.com/v1" : apiBaseUrl.TrimEnd('/');

        /// <summary>Bearer-токен (храните вне репозитория).</summary>
        public string ApiKey => apiKey ?? "";

        /// <summary>Идентификатор модели на стороне провайдера.</summary>
        public string Model => string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;

        /// <summary>Температура сэмплирования.</summary>
        public float Temperature => temperature;

        /// <summary>Таймаут UnityWebRequest в секундах.</summary>
        public int RequestTimeoutSeconds => requestTimeoutSeconds;

        /// <summary>Максимум токенов в ответе.</summary>
        public int MaxTokens => maxTokens;

        /// <summary>Логировать входящие промпты (system, user) и инструменты.</summary>
        public bool LogLlmInput => logLlmInput;

        /// <summary>Логировать исходящие ответы модели и результаты tool calls.</summary>
        public bool LogLlmOutput => logLlmOutput;

        /// <summary>Логировать сырые HTTP request/response JSON.</summary>
        public bool EnableHttpDebugLogging => enableHttpDebugLogging;

        /// <summary>
        /// Для PlayMode-тестов и рантайм-конфигурации без asset (не вызывать из прод-кода без явной политики).
        /// </summary>
        public void SetRuntimeConfiguration(
            bool useOpenAiCompatibleHttp,
            string apiBaseUrl,
            string apiKey,
            string model,
            float temperature = 0.2f,
            int requestTimeoutSeconds = 120,
            int maxTokens = 4096)
        {
            this.useOpenAiCompatibleHttp = useOpenAiCompatibleHttp;
            this.apiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrl) ? "https://api.openai.com/v1" : apiBaseUrl;
            this.apiKey = apiKey ?? "";
            this.model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
            this.temperature = temperature;
            this.requestTimeoutSeconds = requestTimeoutSeconds < 5 ? 5 : requestTimeoutSeconds;
            this.maxTokens = maxTokens < 64 ? 4096 : maxTokens;
        }
    }
}