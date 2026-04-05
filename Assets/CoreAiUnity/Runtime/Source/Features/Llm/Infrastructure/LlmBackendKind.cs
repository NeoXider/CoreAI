namespace CoreAI.Infrastructure.Llm
{
    /// <summary>Тип бэкенда в профиле <see cref="LlmRoutingManifest"/>.</summary>
    public enum LlmBackendKind
    {
        /// <summary>OpenAI-compatible HTTP (<see cref="OpenAiChatLlmClient"/>).</summary>
        OpenAiHttp = 0,

        /// <summary>Локальный LLMUnity (<see cref="MeaiLlmUnityClient"/>).</summary>
        LlmUnity = 1,

        /// <summary>Заглушка (<see cref="StubLlmClient"/>).</summary>
        Stub = 2
    }
}