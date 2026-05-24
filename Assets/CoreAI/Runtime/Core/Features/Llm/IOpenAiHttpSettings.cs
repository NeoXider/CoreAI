using System.Collections.Generic;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Defines the contract for open ai http settings implementations.
    /// </summary>
    public interface IOpenAiHttpSettings
    {
        /// <summary>OpenAI-compatible API base URL.</summary>
        string ApiBaseUrl { get; }

        /// <summary>API key used for hosted LLM providers.</summary>
        string ApiKey { get; }

        /// <summary>
        /// Full Authorization header value. When empty, clients fall back to <see cref="ApiKey"/> as a bearer token.
        /// </summary>
        string AuthorizationHeader { get; }

        /// <summary>Model identifier requested from the LLM backend.</summary>
        string Model { get; }

        /// <summary>Sampling temperature requested from the LLM backend.</summary>
        float Temperature { get; }

        /// <summary>Per-request timeout in seconds.</summary>
        int RequestTimeoutSeconds { get; }

        /// <summary>Maximum number of output tokens requested from the backend.</summary>
        int MaxTokens { get; }

        /// <summary>Gets whether LLM request prompts should be logged.</summary>
        bool LogLlmInput { get; }

        /// <summary>Gets whether LLM responses should be logged.</summary>
        bool LogLlmOutput { get; }

        /// <summary>Gets whether verbose HTTP diagnostics are enabled.</summary>
        bool EnableHttpDebugLogging { get; }

        /// <summary>
        /// Provides additional HTTP headers sent with every request (e.g., tenant-id, request-id, idempotency-key).
        /// Returning null or empty falls back to no extra headers.
        /// </summary>
        IRequestHeaderProvider? HeaderProvider { get; }
    }

    /// <summary>
    /// Supplies custom HTTP headers for OpenAI-compatible requests.
    /// Implementations can be transient (new values per request) or static.
    /// </summary>
    public interface IRequestHeaderProvider
    {
        /// <summary>
        /// Returns headers to add to the outgoing request.
        /// Called once per logical request (retries reuse the same headers via <see cref="IdempotencyKey"/>).
        /// </summary>
        IReadOnlyList<KeyValuePair<string, string>> GetHeaders();

        /// <summary>
        /// A stable idempotency key for this logical request (reused across retries).
        /// If null/empty, a new key is generated automatically.
        /// </summary>
        string IdempotencyKey { get; }

        /// <summary>
        /// Unique identifier for this logical request (trace/diagnostic).
        /// Mapped to X-Request-Id.
        /// </summary>
        string RequestId { get; }
    }
}
