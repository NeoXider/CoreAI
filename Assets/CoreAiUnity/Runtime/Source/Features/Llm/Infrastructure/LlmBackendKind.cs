namespace CoreAI.Infrastructure.Llm
{
    /// <summary>Backend type in a <see cref="LlmRoutingManifest"/> profile.</summary>
    public enum LlmBackendKind
    {
        /// <summary>OpenAI-compatible HTTP (<see cref="OpenAiChatLlmClient"/>).</summary>
        OpenAiHttp = 0,

        /// <summary>Local LLMUnity backend (<see cref="MeaiLlmUnityClient"/>).</summary>
        LlmUnity = 1,

        /// <summary>Deterministic stub backend (<see cref="StubLlmClient"/>).</summary>
        Stub = 2,

        /// <summary>Product-facing local model mode.</summary>
        LocalModel = 10,

        /// <summary>Product-facing client-owned OpenAI-compatible API mode.</summary>
        ClientOwnedApi = 11,

        /// <summary>Product-facing client-limited OpenAI-compatible API mode.</summary>
        ClientLimited = 12,

        /// <summary>Product-facing server-managed backend proxy mode.</summary>
        ServerManagedApi = 13,

        /// <summary>Product-facing offline mode.</summary>
        Offline = 14
    }
}