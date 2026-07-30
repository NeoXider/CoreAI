using System.Collections.Generic;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Provider-side reasoning/thinking request mode for OpenAI-compatible APIs.
    /// </summary>
    public enum LlmReasoningMode
    {
        /// <summary>Do not send provider-specific thinking controls.</summary>
        ProviderDefault = 0,

        /// <summary>Ask compatible providers to disable thinking.</summary>
        Disabled = 1,

        /// <summary>Ask compatible providers to enable thinking.</summary>
        Enabled = 2
    }

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

        /// <summary>
        /// Model identifier requested from the LLM backend. May be empty ONLY under
        /// <see cref="LlmExecutionMode.ServerManagedApi"/>, where the backend picks the model and the
        /// field is omitted from the request body; every other mode treats an empty name as a
        /// configuration error rather than substituting a default.
        /// </summary>
        string Model { get; }

        /// <summary>
        /// Product-facing execution mode of this HTTP profile. Defaults to
        /// <see cref="LlmExecutionMode.ClientOwnedApi"/> so an implementation that does not model the
        /// mode keeps the strict "an explicit model is required" contract.
        /// </summary>
        LlmExecutionMode ExecutionMode => LlmExecutionMode.ClientOwnedApi;

        /// <summary>Sampling temperature requested from the LLM backend.</summary>
        float Temperature { get; }

        /// <summary>Per-request timeout in seconds.</summary>
        int RequestTimeoutSeconds { get; }

        /// <summary>Maximum number of output tokens requested from the backend.</summary>
        int MaxTokens { get; }

        /// <summary>
        /// Optional raw JSON object merged into OpenAI-compatible request bodies.
        /// Use for provider-specific fields that are not part of the standard OpenAI schema.
        /// Empty string disables this extension point.
        /// </summary>
        string ExtraBodyJson => "";

        /// <summary>
        /// Provider-specific thinking mode. ProviderDefault leaves request bodies unchanged.
        /// </summary>
        LlmReasoningMode ReasoningMode => LlmReasoningMode.ProviderDefault;

        /// <summary>
        /// When true, request bodies include reasoning/thinking controls for compatible providers.
        /// </summary>
        bool SendReasoningControls => ReasoningMode != LlmReasoningMode.ProviderDefault;

        /// <summary>
        /// Desired thinking mode when <see cref="SendReasoningControls"/> is true.
        /// </summary>
        bool EnableReasoning => ReasoningMode == LlmReasoningMode.Enabled;

        /// <summary>
        /// Optional provider-specific thinking budget in tokens. Zero disables the field.
        /// </summary>
        int ThinkingBudgetTokens => 0;

        /// <summary>True when request prompts should be written to diagnostic logs.</summary>
        bool LogLlmInput { get; }

        /// <summary>True when model responses should be written to diagnostic logs.</summary>
        bool LogLlmOutput { get; }

        /// <summary>True when raw HTTP diagnostics are enabled for troubleshooting.</summary>
        bool EnableHttpDebugLogging { get; }

        /// <summary>
        /// Supplies additional HTTP headers sent with every request, such as tenant ids,
        /// request ids, or idempotency keys.
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
