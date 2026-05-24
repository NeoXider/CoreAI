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
        public string Model { get; set; } = "gpt-4o-mini";
        public float Temperature { get; set; } = 0.2f;
        public int RequestTimeoutSeconds { get; set; } = 120;
        public int MaxTokens { get; set; } = 2048;
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
