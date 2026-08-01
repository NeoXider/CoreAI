#if COREAI_LLM
using CoreAI.Ai;
using CoreAI.Config;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// <see cref="IOpenAiHttpSettings"/> that points CoreAI's native OpenAI HTTP pipeline at the
    /// LLMUnity built-in server (started in-process via <c>LLM.remote = true</c> + <see cref="CoreAISettingsAsset.LlmUnityServerPort"/>).
    /// Overrides only the endpoint/model/auth; every other knob (temperature, timeout, max tokens,
    /// reasoning, logging) is delegated to <see cref="CoreAISettingsAsset"/> so the local-server path
    /// behaves identically to any other OpenAI-compatible backend. The LLMUnity server exposes
    /// <c>POST /v1/chat/completions</c> with native <c>tools</c>/<c>tool_calls</c> and SSE streaming;
    /// it does NOT implement <c>/v1/models</c>, so the model name is supplied explicitly here.
    /// </summary>
    public sealed class LlmUnityServerHttpSettings : IOpenAiHttpSettings
    {
        private readonly CoreAISettingsAsset _s;
        private readonly string _baseUrl;
        private readonly string _model;
        private readonly string _apiKey;

        public LlmUnityServerHttpSettings(CoreAISettingsAsset settings, int port, string modelName, string apiKey = "")
        {
            _s = settings;
            _baseUrl = $"http://localhost:{port}/v1";
            _model = string.IsNullOrWhiteSpace(modelName) ? "local" : modelName;
            _apiKey = apiKey ?? "";
        }

        public string ApiBaseUrl => _baseUrl;
        public string ApiKey => _apiKey;
        public string AuthorizationHeader => "";
        public string Model => _model;
        public float Temperature => _s.Temperature;
        public int RequestTimeoutSeconds => _s.EffectiveHttpRequestTimeoutSeconds;
        public int MaxTokens => _s.MaxTokens;
        public string ExtraBodyJson => _s.ExtraBodyJson;
        public LlmReasoningMode ReasoningMode => _s.ReasoningMode;
        public int ThinkingBudgetTokens => _s.ThinkingBudgetTokens;
        public bool LogLlmInput => _s.LogLlmInput;
        public bool LogLlmOutput => _s.LogLlmOutput;
        public bool EnableHttpDebugLogging => _s.EnableHttpDebugLogging;

        public IRequestHeaderProvider? HeaderProvider => null;
    }
}
#endif
