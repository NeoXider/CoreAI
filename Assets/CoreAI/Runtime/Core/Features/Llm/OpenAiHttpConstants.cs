namespace CoreAI.Infrastructure.Llm
{
    /// <summary>OpenAI-compatible HTTP defaults shared across clients and host tooling.</summary>
    public static class OpenAiHttpConstants
    {
        public const string DefaultApiBaseUrl = "https://api.openai.com/v1";
        public const string HttpRefererHeaderName = "HTTP-Referer";
        public const string HttpRefererUnityUrl = "https://unity.com";
    }
}
