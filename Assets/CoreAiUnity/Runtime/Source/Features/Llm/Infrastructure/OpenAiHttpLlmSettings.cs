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
        [Tooltip("When enabled, this profile binds an HTTP OpenAI-compatible client instead of LLMUnity.")]
        [SerializeField]
        private bool useOpenAiCompatibleHttp;

        [SerializeField]
        private LlmExecutionMode executionMode = LlmExecutionMode.ClientOwnedApi;

        [Tooltip(
            "Base URL without trailing slash (e.g., https://api.openai.com/v1 or http://localhost:1234/v1 for LM Studio).")]
        [SerializeField]
        private string apiBaseUrl = OpenAiHttpConstants.DefaultApiBaseUrl;

        [SerializeField]
        private string apiKey = "";

        [SerializeField]
        private string model = "gpt-4o-mini";

        [SerializeField]
        [Range(0f, 2f)]
        private float temperature = 0.2f;

        [SerializeField]
        [Min(5)]
        private int requestTimeoutSeconds = 120;

        [SerializeField]
        [Min(64)]
        private int maxTokens = 128000;

        [Header("Provider-specific request body")]
        [Tooltip("Raw JSON object merged into each OpenAI-compatible request body. Leave empty for standard requests.")]
        [SerializeField]
        [TextArea(2, 6)]
        private string extraBodyJson = "";

        [Tooltip(
            "Provider Default leaves request bodies unchanged. Disabled/Enabled sends provider-specific thinking controls.")]
        [SerializeField]
        private LlmReasoningMode reasoningMode = LlmReasoningMode.ProviderDefault;

        [Tooltip("Optional thinking budget for compatible providers. 0 = omit.")]
        [SerializeField]
        [Min(0)]
        private int thinkingBudgetTokens;

        [Header("Client limits")]
        [SerializeField]
        [Min(0)]
        private int maxRequestsPerSession;

        [SerializeField]
        [Min(0)]
        private int maxPromptChars;

        [Header("🔧 Debug")]
        [Tooltip("Log outbound prompts/tool definitions.")]
        [SerializeField]
        private bool logLlmInput = true;

        [Tooltip("Log assistant payloads and aggregated tool summaries.")]
        [SerializeField]
        private bool logLlmOutput = true;

        [Tooltip("Dump raw HTTP JSON (development only).")]
        [SerializeField]
        private bool enableHttpDebugLogging = false;

        /// <summary>Whether this profile should create an HTTP client.</summary>
        public bool UseOpenAiCompatibleHttp => useOpenAiCompatibleHttp;

        /// <summary>Product-facing execution mode for this HTTP profile.</summary>
        public LlmExecutionMode ExecutionMode => NormalizeHttpMode(executionMode);

        /// <summary>Base API URL without a trailing slash.</summary>
        public string ApiBaseUrl =>
            string.IsNullOrWhiteSpace(apiBaseUrl) ? OpenAiHttpConstants.DefaultApiBaseUrl : apiBaseUrl.TrimEnd('/');

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

        /// <summary>Provider-specific extra request body JSON.</summary>
        public string ExtraBodyJson => extraBodyJson ?? "";

        /// <summary>Provider-specific reasoning mode.</summary>
        public LlmReasoningMode ReasoningMode => reasoningMode;

        /// <summary>Optional provider-side thinking budget in tokens.</summary>
        public int ThinkingBudgetTokens => thinkingBudgetTokens < 0 ? 0 : thinkingBudgetTokens;

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

        public IRequestHeaderProvider? HeaderProvider => null;

        /// <summary>
        /// Builds a Unity-free HTTP settings snapshot for runtime clients and tests.
        /// </summary>
        public OpenAiHttpOptions ToOptions()
        {
            return new OpenAiHttpOptions
            {
                UseOpenAiCompatibleHttp = UseOpenAiCompatibleHttp,
                ExecutionMode = ExecutionMode,
                ApiBaseUrl = ApiBaseUrl,
                ApiKey = ApiKey,
                AuthorizationHeader = AuthorizationHeader,
                Model = Model,
                Temperature = Temperature,
                RequestTimeoutSeconds = RequestTimeoutSeconds,
                MaxTokens = MaxTokens,
                ExtraBodyJson = ExtraBodyJson,
                ReasoningMode = ReasoningMode,
                ThinkingBudgetTokens = ThinkingBudgetTokens,
                MaxRequestsPerSession = MaxRequestsPerSession,
                MaxPromptChars = MaxPromptChars,
                LogLlmInput = LogLlmInput,
                LogLlmOutput = LogLlmOutput,
                EnableHttpDebugLogging = EnableHttpDebugLogging,
                HeaderProvider = HeaderProvider
            };
        }

        /// <summary>
        /// Copies portable HTTP settings into this Unity authoring asset.
        /// </summary>
        public void ApplyOptions(OpenAiHttpOptions options)
        {
            if (options == null)
            {
                return;
            }

            useOpenAiCompatibleHttp = options.UseOpenAiCompatibleHttp;
            executionMode = NormalizeHttpMode(options.ExecutionMode);
            apiBaseUrl = string.IsNullOrWhiteSpace(options.ApiBaseUrl)
                ? OpenAiHttpConstants.DefaultApiBaseUrl
                : options.ApiBaseUrl;
            apiKey = options.ApiKey ?? "";
            model = string.IsNullOrWhiteSpace(options.Model) ? "gpt-4o-mini" : options.Model;
            temperature = Mathf.Clamp(options.Temperature, 0f, 2f);
            requestTimeoutSeconds = options.RequestTimeoutSeconds < 5 ? 5 : options.RequestTimeoutSeconds;
            maxTokens = options.MaxTokens < 64 ? 128000 : options.MaxTokens;
            extraBodyJson = options.ExtraBodyJson ?? "";
            reasoningMode = options.ReasoningMode;
            thinkingBudgetTokens = options.ThinkingBudgetTokens < 0 ? 0 : options.ThinkingBudgetTokens;
            maxRequestsPerSession = options.MaxRequestsPerSession < 0 ? 0 : options.MaxRequestsPerSession;
            maxPromptChars = options.MaxPromptChars < 0 ? 0 : options.MaxPromptChars;
            logLlmInput = options.LogLlmInput;
            logLlmOutput = options.LogLlmOutput;
            enableHttpDebugLogging = options.EnableHttpDebugLogging;
        }

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
            int maxTokens = 128000,
            LlmExecutionMode executionMode = LlmExecutionMode.ClientOwnedApi,
            int maxRequestsPerSession = 0,
            int maxPromptChars = 0)
        {
            this.useOpenAiCompatibleHttp = useOpenAiCompatibleHttp;
            this.executionMode = NormalizeHttpMode(executionMode);
            this.apiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrl)
                ? OpenAiHttpConstants.DefaultApiBaseUrl
                : apiBaseUrl;
            this.apiKey = apiKey ?? "";
            this.model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
            this.temperature = temperature;
            this.requestTimeoutSeconds = requestTimeoutSeconds < 5 ? 5 : requestTimeoutSeconds;
            this.maxTokens = maxTokens < 64 ? 128000 : maxTokens;
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
