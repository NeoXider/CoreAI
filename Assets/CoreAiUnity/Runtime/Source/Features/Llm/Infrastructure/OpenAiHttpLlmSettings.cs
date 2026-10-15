using CoreAI.Ai;
using Newtonsoft.Json.Linq;
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

        [Tooltip(
            "Provider model id. Required for client-owned modes - there is no built-in default. " +
            "Leave empty under ServerManagedApi: the backend picks the model.")]
        [SerializeField]
        private string model = "";

        [SerializeField]
        [Range(0f, 2f)]
        private float temperature = 0.2f;

        [SerializeField]
        private bool overrideTemperature = true;

        [SerializeField]
        [Min(0)]
        private int requestTimeoutSeconds;

        [Tooltip("Response wait in seconds. -1 inherits the global setting; zero disables it.")]
        [SerializeField]
        private int responseWaitTimeoutSeconds = -1;

        [SerializeField]
        [Min(0)]
        private int maxTokens = 262144;

        [SerializeField]
        private bool overrideMaxTokens = true;

        [Header("Provider-specific request body")]
        [Tooltip("Raw JSON object merged into each OpenAI-compatible request body. Leave empty for standard requests.")]
        [SerializeField]
        [TextArea(2, 6)]
        private string extraBodyJson = "";

        [Tooltip(
            "Provider Default leaves request bodies unchanged. Disabled/Enabled sends provider-specific thinking controls.")]
        [SerializeField]
        private LlmReasoningMode reasoningMode = LlmReasoningMode.ProviderDefault;

        [SerializeField]
        private LlmReasoningEffort reasoningEffort = LlmReasoningEffort.Default;

        [SerializeField]
        private LlmReasoningDialect reasoningDialect = LlmReasoningDialect.Auto;

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

        /// <summary>
        /// Provider-side model identifier. Empty means "not configured": under
        /// <see cref="LlmExecutionMode.ServerManagedApi"/> the backend picks the model, in every other mode
        /// the request fails with a configuration error. No built-in model is substituted here.
        /// </summary>
        public string Model => string.IsNullOrWhiteSpace(model) ? "" : model.Trim();

        /// <summary>Sampling temperature.</summary>
        public float Temperature => temperature;

        /// <summary>Whether this profile overrides inherited sampling temperature.</summary>
        public bool OverrideTemperature => overrideTemperature;

        /// <summary>Endpoint sampling override; null inherits global settings.</summary>
        public float? TemperatureOverride => overrideTemperature ? temperature : null;

        /// <summary>UnityWebRequest timeout in seconds.</summary>
        public int RequestTimeoutSeconds => requestTimeoutSeconds;

        /// <summary>Response wait override; null inherits the global setting and zero disables it.</summary>
        public int? ResponseWaitTimeoutSeconds => responseWaitTimeoutSeconds < 0 ? null : responseWaitTimeoutSeconds;

        /// <summary>Maximum response tokens.</summary>
        public int MaxTokens => maxTokens;

        /// <summary>Whether this profile overrides inherited response limits.</summary>
        public bool OverrideMaxTokens => overrideMaxTokens;

        /// <summary>Endpoint response token cap; zero is unlimited and null inherits global settings.</summary>
        public int? MaxTokensOverride => overrideMaxTokens ? maxTokens : null;

        /// <summary>Provider-specific extra request body JSON.</summary>
        public string ExtraBodyJson => extraBodyJson ?? "";

        /// <summary>
        /// Safely sets a provider-specific request-body parameter. Passing C# <see langword="null"/> removes it;
        /// use <see cref="JValue.CreateNull"/> to send JSON <c>null</c>. Invalid input leaves the asset unchanged.
        /// </summary>
        public void SetProviderBodyParameter(string key, JToken value)
        {
            string normalized = OpenAiProviderBodyParameters.Set(extraBodyJson, key, value);
            extraBodyJson = normalized;
        }

        /// <summary>Safely removes a provider-specific request-body parameter.</summary>
        public void RemoveProviderBodyParameter(string key)
        {
            string normalized = OpenAiProviderBodyParameters.Remove(extraBodyJson, key);
            extraBodyJson = normalized;
        }

        /// <summary>Provider-specific reasoning mode.</summary>
        public LlmReasoningMode ReasoningMode => reasoningMode;

        /// <summary>Typed reasoning effort; Default inherits provider or legacy settings.</summary>
        public LlmReasoningEffort ReasoningEffort => reasoningEffort;

        /// <summary>Wire dialect used for provider reasoning controls.</summary>
        public LlmReasoningDialect ReasoningDialect => reasoningDialect;

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
                OverrideTemperature = OverrideTemperature,
                RequestTimeoutSeconds = RequestTimeoutSeconds,
                ResponseWaitTimeoutSeconds = ResponseWaitTimeoutSeconds,
                MaxTokens = MaxTokens,
                OverrideMaxTokens = OverrideMaxTokens,
                ExtraBodyJson = ExtraBodyJson,
                ReasoningMode = ReasoningMode,
                ReasoningEffort = ReasoningEffort,
                ReasoningDialect = ReasoningDialect,
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
            model = options.Model ?? "";
            temperature = Mathf.Clamp(options.Temperature, 0f, 2f);
            overrideTemperature = options.OverrideTemperature;
            requestTimeoutSeconds = System.Math.Max(0, options.RequestTimeoutSeconds);
            responseWaitTimeoutSeconds = options.ResponseWaitTimeoutSeconds ?? -1;
            maxTokens = options.MaxTokens < 0 ? 262144 : options.MaxTokens;
            overrideMaxTokens = options.OverrideMaxTokens;
            extraBodyJson = options.ExtraBodyJson ?? "";
            reasoningMode = options.ReasoningMode;
            reasoningEffort = options.ReasoningEffort;
            reasoningDialect = options.ReasoningDialect;
            thinkingBudgetTokens = options.ThinkingBudgetTokens < 0 ? 0 : options.ThinkingBudgetTokens;
            maxRequestsPerSession = options.MaxRequestsPerSession < 0 ? 0 : options.MaxRequestsPerSession;
            maxPromptChars = options.MaxPromptChars < 0 ? 0 : options.MaxPromptChars;
            logLlmInput = options.LogLlmInput;
            logLlmOutput = options.LogLlmOutput;
            enableHttpDebugLogging = options.EnableHttpDebugLogging;
        }

        /// <summary>Preserves the original full-parameter runtime configuration API.</summary>
        public void SetRuntimeConfiguration(bool useOpenAiCompatibleHttp, string apiBaseUrl,
            string apiKey, string model, float temperature, int requestTimeoutSeconds, int maxTokens,
            LlmExecutionMode executionMode, int maxRequestsPerSession, int maxPromptChars)
        {
            SetRuntimeConfiguration(useOpenAiCompatibleHttp, apiBaseUrl, apiKey, model, temperature,
                requestTimeoutSeconds, maxTokens, executionMode, maxRequestsPerSession, maxPromptChars,
                LlmReasoningEffort.Default, LlmReasoningDialect.Auto, true, true);
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
            int requestTimeoutSeconds = 0,
            int maxTokens = 262144,
            LlmExecutionMode executionMode = LlmExecutionMode.ClientOwnedApi,
            int maxRequestsPerSession = 0,
            int maxPromptChars = 0,
            LlmReasoningEffort reasoningEffort = LlmReasoningEffort.Default,
            LlmReasoningDialect reasoningDialect = LlmReasoningDialect.Auto,
            bool overrideTemperature = true,
            bool overrideMaxTokens = true)
        {
            this.useOpenAiCompatibleHttp = useOpenAiCompatibleHttp;
            this.executionMode = NormalizeHttpMode(executionMode);
            this.apiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrl)
                ? OpenAiHttpConstants.DefaultApiBaseUrl
                : apiBaseUrl;
            this.apiKey = apiKey ?? "";
            this.model = model ?? "";
            this.temperature = temperature;
            this.overrideTemperature = overrideTemperature;
            this.requestTimeoutSeconds = System.Math.Max(0, requestTimeoutSeconds);
            this.maxTokens = maxTokens < 0 ? 262144 : maxTokens;
            this.overrideMaxTokens = overrideMaxTokens;
            this.reasoningEffort = reasoningEffort;
            this.reasoningDialect = reasoningDialect;
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
