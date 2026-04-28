namespace CoreAI.Ai
{
    /// <summary>
    /// Describes the product-facing execution mode used to select an LLM backend for one request or routing profile.
    /// </summary>
    public enum LlmExecutionMode
    {
        /// <summary>
        /// Uses the legacy backend selection rules for existing scenes and serialized assets.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Uses a local model through LLMUnity when the current platform and scene support it.
        /// </summary>
        LocalModel = 1,

        /// <summary>
        /// Uses an OpenAI-compatible HTTP endpoint with a provider key owned by the application user or developer.
        /// </summary>
        ClientOwnedApi = 2,

        /// <summary>
        /// Uses an OpenAI-compatible HTTP endpoint with local client-side usage limits or policy checks.
        /// </summary>
        ClientLimited = 3,

        /// <summary>
        /// Uses a game backend or proxy that owns provider credentials and keeps them out of the client build.
        /// </summary>
        ServerManagedApi = 4,

        /// <summary>
        /// Uses deterministic offline responses for tests, demos, or builds without live LLM access.
        /// </summary>
        Offline = 5
    }
}
