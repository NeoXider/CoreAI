using System.Collections.Generic;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Mutable Unity-free OpenAI-compatible HTTP settings.
    /// </summary>
    public sealed class OpenAiHttpOptions : IOpenAiHttpSettings
    {
        public bool UseOpenAiCompatibleHttp { get; set; }
        public LlmExecutionMode ExecutionMode { get; set; } = LlmExecutionMode.ClientOwnedApi;
        public string ApiBaseUrl { get; set; } = OpenAiHttpConstants.DefaultApiBaseUrl;
        public string ApiKey { get; set; } = "";
        public string AuthorizationHeader { get; set; } = "";
        /// <summary>
        /// Provider model id. Empty is "not configured" — legal only under
        /// <see cref="LlmExecutionMode.ServerManagedApi"/>, where the backend picks the model. There is no
        /// built-in default: a silent one bills a model nobody selected and then lies about it in logs.
        /// </summary>
        public string Model { get; set; } = "";
        public float Temperature { get; set; } = 0.2f;
        public int RequestTimeoutSeconds { get; set; } = 120;
        public int MaxTokens { get; set; } = 128000;
        public string ExtraBodyJson { get; set; } = "";
        public LlmReasoningMode ReasoningMode { get; set; } = LlmReasoningMode.ProviderDefault;
        public int ThinkingBudgetTokens { get; set; }
        public int MaxRequestsPerSession { get; set; }
        public int MaxPromptChars { get; set; }
        public bool LogLlmInput { get; set; } = true;
        public bool LogLlmOutput { get; set; } = true;
        public bool EnableHttpDebugLogging { get; set; }
        public IRequestHeaderProvider HeaderProvider { get; set; }

        public static OpenAiHttpOptions From(IOpenAiHttpSettings source)
        {
            if (source == null)
            {
                return new OpenAiHttpOptions();
            }

            bool useOpenAiCompatibleHttp = true;
            LlmExecutionMode executionMode = LlmExecutionMode.ClientOwnedApi;
            int maxRequests = 0;
            int maxPromptChars = 0;
            if (source is OpenAiHttpOptions options)
            {
                useOpenAiCompatibleHttp = options.UseOpenAiCompatibleHttp;
                executionMode = options.ExecutionMode;
                maxRequests = options.MaxRequestsPerSession;
                maxPromptChars = options.MaxPromptChars;
            }

            return new OpenAiHttpOptions
            {
                UseOpenAiCompatibleHttp = useOpenAiCompatibleHttp,
                ExecutionMode = executionMode,
                ApiBaseUrl = source.ApiBaseUrl,
                ApiKey = source.ApiKey,
                AuthorizationHeader = source.AuthorizationHeader,
                Model = source.Model,
                Temperature = source.Temperature,
                RequestTimeoutSeconds = source.RequestTimeoutSeconds,
                MaxTokens = source.MaxTokens,
                ExtraBodyJson = source.ExtraBodyJson,
                ReasoningMode = source.ReasoningMode,
                ThinkingBudgetTokens = source.ThinkingBudgetTokens,
                MaxRequestsPerSession = maxRequests,
                MaxPromptChars = maxPromptChars,
                LogLlmInput = source.LogLlmInput,
                LogLlmOutput = source.LogLlmOutput,
                EnableHttpDebugLogging = source.EnableHttpDebugLogging,
                HeaderProvider = source.HeaderProvider
            };
        }
    }
}
