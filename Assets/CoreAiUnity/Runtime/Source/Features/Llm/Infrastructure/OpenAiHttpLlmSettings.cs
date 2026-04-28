using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Profile-specific OpenAI-compatible HTTP settings for client-owned, client-limited, and server-managed modes.
    /// </summary>
    [CreateAssetMenu(menuName = "CoreAI/LLM/OpenAI-compatible HTTP", fileName = "OpenAiHttpLlmSettings")]
    public sealed class OpenAiHttpLlmSettings : ScriptableObject, IOpenAiHttpSettings
    {
        [Tooltip("Если включено, ILlmClient ходит в HTTP chat/completions вместо LLMAgent на сцене.")] [SerializeField]
        private bool useOpenAiCompatibleHttp;

        [SerializeField] private LlmExecutionMode executionMode = LlmExecutionMode.ClientOwnedApi;

        [Tooltip(
            "База без завершающего слэша, например https://api.openai.com/v1 или http://localhost:1234/v1 (LM Studio).")]
        [SerializeField]
        private string apiBaseUrl = "https://api.openai.com/v1";

        [SerializeField] private string apiKey = "";

        [SerializeField] private string model = "gpt-4o-mini";

        [SerializeField] [Range(0f, 2f)] private float temperature = 0.2f;

        [SerializeField] [Min(5)] private int requestTimeoutSeconds = 120;

        [SerializeField] [Min(64)] private int maxTokens = 4096;

        [Header("Client limits")]
        [SerializeField] [Min(0)] private int maxRequestsPerSession;

        [SerializeField] [Min(0)] private int maxPromptChars;

        [Header("🔧 Отладка")] [Tooltip("Логировать входящие промпты (system, user) и инструменты.")] [SerializeField]
        private bool logLlmInput = true;

        [Tooltip("Логировать исходящие ответы модели и результаты tool calls.")] [SerializeField]
        private bool logLlmOutput = true;

        [Tooltip("Логировать сырые HTTP request/response JSON.")] [SerializeField]
        private bool enableHttpDebugLogging = false;

        /// <summary>Whether this profile should create an HTTP client.</summary>
        public bool UseOpenAiCompatibleHttp => useOpenAiCompatibleHttp;

        /// <summary>Product-facing execution mode for this HTTP profile.</summary>
        public LlmExecutionMode ExecutionMode => NormalizeHttpMode(executionMode);

        /// <summary>Base API URL without a trailing slash.</summary>
        public string ApiBaseUrl =>
            string.IsNullOrWhiteSpace(apiBaseUrl) ? "https://api.openai.com/v1" : apiBaseUrl.TrimEnd('/');

        /// <summary>Bearer token for provider-owned or backend-owned authorization.</summary>
        public string ApiKey => apiKey ?? "";

        /// <summary>Full Authorization header value. Empty means use <see cref="ApiKey"/> as bearer token.</summary>
        public string AuthorizationHeader => "";

        /// <summary>Provider-side model identifier.</summary>
        public string Model => string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;

        /// <summary>Sampling temperature.</summary>
        public float Temperature => temperature;

        /// <summary>UnityWebRequest timeout in seconds.</summary>
        public int RequestTimeoutSeconds => requestTimeoutSeconds;

        /// <summary>Maximum response tokens.</summary>
        public int MaxTokens => maxTokens;

        /// <summary>Maximum LLM requests allowed in the current session; zero disables this limit.</summary>
        public int MaxRequestsPerSession => maxRequestsPerSession < 0 ? 0 : maxRequestsPerSession;

        /// <summary>Maximum prompt characters allowed per request; zero disables this limit.</summary>
        public int MaxPromptChars => maxPromptChars < 0 ? 0 : maxPromptChars;

        /// <summary>Log inbound prompts and tools.</summary>
        public bool LogLlmInput => logLlmInput;

        /// <summary>Log outbound model responses and tool results.</summary>
        public bool LogLlmOutput => logLlmOutput;

        /// <summary>Log raw HTTP request and response JSON.</summary>
        public bool EnableHttpDebugLogging => enableHttpDebugLogging;

        /// <summary>
        /// Configures this profile at runtime for tests and dynamic setup.
        /// </summary>
        public void SetRuntimeConfiguration(
            bool useOpenAiCompatibleHttp,
            string apiBaseUrl,
            string apiKey,
            string model,
            float temperature = 0.2f,
            int requestTimeoutSeconds = 120,
            int maxTokens = 4096,
            LlmExecutionMode executionMode = LlmExecutionMode.ClientOwnedApi,
            int maxRequestsPerSession = 0,
            int maxPromptChars = 0)
        {
            this.useOpenAiCompatibleHttp = useOpenAiCompatibleHttp;
            this.executionMode = NormalizeHttpMode(executionMode);
            this.apiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrl) ? "https://api.openai.com/v1" : apiBaseUrl;
            this.apiKey = apiKey ?? "";
            this.model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
            this.temperature = temperature;
            this.requestTimeoutSeconds = requestTimeoutSeconds < 5 ? 5 : requestTimeoutSeconds;
            this.maxTokens = maxTokens < 64 ? 4096 : maxTokens;
            this.maxRequestsPerSession = maxRequestsPerSession < 0 ? 0 : maxRequestsPerSession;
            this.maxPromptChars = maxPromptChars < 0 ? 0 : maxPromptChars;
        }

        private static LlmExecutionMode NormalizeHttpMode(LlmExecutionMode mode)
        {
            return mode == LlmExecutionMode.ClientLimited || mode == LlmExecutionMode.ServerManagedApi
                ? mode
                : LlmExecutionMode.ClientOwnedApi;
        }
    }
}