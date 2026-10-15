using System;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>Resolves provider dialects without network access or host dependencies.</summary>
    public static class LlmReasoningDialectResolver
    {
        /// <summary>Resolves Auto using an endpoint host, then the configured reasoning controls.</summary>
        public static LlmReasoningDialect Resolve(string apiBaseUrl, LlmReasoningDialect dialect,
            LlmReasoningEffort effort, LlmReasoningMode legacyMode = LlmReasoningMode.ProviderDefault)
        {
            if (dialect != LlmReasoningDialect.Auto)
            {
                return dialect;
            }

            string host = Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out Uri endpoint)
                ? endpoint.Host.ToLowerInvariant() : "";
            if (host == "api.openai.com" || host.EndsWith(".openai.azure.com", StringComparison.Ordinal))
            {
                return LlmReasoningDialect.OpenAi;
            }

            if (host == "openrouter.ai")
            {
                return LlmReasoningDialect.OpenRouter;
            }

            if (host == "generativelanguage.googleapis.com")
            {
                return LlmReasoningDialect.GeminiCompat;
            }

            if (host == "api.anthropic.com")
            {
                return LlmReasoningDialect.AnthropicCompat;
            }

            if (host.StartsWith("dashscope", StringComparison.Ordinal) ||
                host == "llmunity" || host.EndsWith(".llmunity", StringComparison.Ordinal) ||
                (apiBaseUrl?.IndexOf("llmunity", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
            {
                return LlmReasoningDialect.QwenTemplate;
            }

            return effort != LlmReasoningEffort.Default
                ? LlmReasoningDialect.PassthroughEffort : LlmReasoningDialect.QwenTemplate;
        }
    }
}
