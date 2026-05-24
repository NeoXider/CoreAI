using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Stable error category for LLM failures that UI, retry policies, and backend integrations can handle without parsing strings.
    /// </summary>
    public enum LlmErrorCode
    {
        /// <summary>No error was reported.</summary>
        None = 0,

        /// <summary>The request exceeded its timeout.</summary>
        Timeout = 1,

        /// <summary>The caller cancelled the request.</summary>
        Cancelled = 2,

        /// <summary>The provider returned no usable content.</summary>
        EmptyResponse = 3,

        /// <summary>Authorization failed or the token has expired.</summary>
        AuthExpired = 4,

        /// <summary>The backend or local limiter rejected the request because quota was exhausted.</summary>
        QuotaExceeded = 5,

        /// <summary>The provider or backend rate limited the request.</summary>
        RateLimited = 6,

        /// <summary>The provider or backend is unavailable.</summary>
        BackendUnavailable = 7,

        /// <summary>The request was malformed or rejected by validation.</summary>
        InvalidRequest = 8,

        /// <summary>The provider returned an error that does not fit a more specific category.</summary>
        ProviderError = 9,

        /// <summary>Routing could not resolve a usable backend.</summary>
        RoutingError = 10,

        /// <summary>The request exceeded the model or provider context window.</summary>
        ContextLengthExceeded = 11
    }

    /// <summary>
    /// Exception type used by LLM adapters to preserve structured failure details across abstraction layers.
    /// </summary>
    public sealed class LlmClientException : Exception
    {
        /// <summary>Creates an exception with structured LLM failure metadata.</summary>
        public LlmClientException(
            string message,
            LlmErrorCode errorCode = LlmErrorCode.ProviderError,
            int? httpStatus = null,
            int? retryAfterSeconds = null,
            string providerErrorBody = null)
            : base(message ?? "")
        {
            ErrorCode = errorCode;
            HttpStatus = httpStatus;
            RetryAfterSeconds = retryAfterSeconds;
            ProviderErrorBody = providerErrorBody ?? "";
        }

        /// <summary>Stable failure category.</summary>
        public LlmErrorCode ErrorCode { get; }

        /// <summary>HTTP status code when the failure came from an HTTP transport.</summary>
        public int? HttpStatus { get; }

        /// <summary>Provider hint for when the request may be retried.</summary>
        public int? RetryAfterSeconds { get; }

        /// <summary>Raw provider error body, intended for diagnostics only.</summary>
        public string ProviderErrorBody { get; }
    }

    /// <summary>
    /// Raised when the chat/orchestrator layer cancels the request due to <see cref="ICoreAISettings.LlmRequestTimeoutSeconds"/>
    /// while the caller's own <see cref="CancellationToken"/> was not cancelled (library timeout, not user stop).
    /// Inherits <see cref="OperationCanceledException"/> so existing cancellation handlers still run; use
    /// <c>is LlmOperationTimeoutException</c> or <see cref="Messaging.LlmRequestCompleted"/> <c>ErrorCode</c> to distinguish.
    /// </summary>
    public sealed class LlmOperationTimeoutException : OperationCanceledException
    {
        public LlmOperationTimeoutException()
            : base("LLM request timed out.")
        {
        }
    }

    /// <summary>Input for one <see cref="ILlmClient.CompleteAsync"/> call: role, prompts, tracing.</summary>
    public sealed class LlmCompletionRequest
    {
        /// <summary>Role id for backend routing and system prompt selection.</summary>
        public string AgentRoleId { get; set; } = "";

        /// <summary>System instruction for the model.</summary>
        public string SystemPrompt { get; set; } = "";

        /// <summary>Optional user-facing payload when <see cref="ChatHistory"/> is absent or supplementary.</summary>
        public string UserPayload { get; set; } = "";

        /// <summary>Optional MEAI chat history.</summary>
        public IList<Microsoft.Extensions.AI.ChatMessage> ChatHistory { get; set; }

        /// <summary>End-to-end trace id (orchestrator / LLM decorator / command router).</summary>
        public string TraceId { get; set; } = "";

        /// <summary>
        /// Stable idempotency token for this logical turn (HTTP <c>Idempotency-Key</c>).
        /// Leave empty to auto-assign once per request object; preserved across decorator retries.
        /// </summary>
        public string IdempotencyKey { get; set; } = "";

        /// <summary>Short backend label after role routing (LLM logs).</summary>
        public string RoutingProfileId { get; set; } = "";

        /// <summary>Context budget in tokens (default 8192; routing may override).</summary>
        public int ContextWindowTokens { get; set; } = 8192;

        /// <summary>Optional max completion tokens for the model.</summary>
        public int? MaxOutputTokens { get; set; }

        /// <summary>
        /// Temperature.
        /// </summary>
        public float Temperature { get; set; } = 0.1f;

        /// <summary>
        /// When <c>true</c>, <see cref="Temperature"/> is passed to backends (MEAI / HTTP). When <c>false</c>,
        /// temperature is omitted so providers and LLMUnity use their defaults.
        /// </summary>
        public bool SendTemperature { get; set; }

        /// <summary>Tools exposed to the model for this request.</summary>
        public IReadOnlyList<ILlmTool> Tools { get; set; }

        /// <summary>Optional per-request allowlist of tool names after orchestrator filtering.</summary>
        public IReadOnlyList<string> AllowedToolNames { get; set; }

        /// <summary>Allow the same tool with identical args back-to-back; <c>null</c> defers to global settings.</summary>
        public bool? AllowDuplicateToolCalls { get; set; }

        /// <summary>
        /// How the LLM backend should treat tool selection for this request.
        /// <see cref="LlmToolChoiceMode.Auto"/> = provider default (model decides).
        /// Adapters in the Unity layer translate this to <c>ChatOptions.ToolMode</c>
        /// (Microsoft.Extensions.AI) or to the equivalent provider-native field.
        /// </summary>
        public LlmToolChoiceMode ForcedToolMode { get; set; } = LlmToolChoiceMode.Auto;

        /// <summary>
        /// Tool name to require when <see cref="ForcedToolMode"/> is
        /// <see cref="LlmToolChoiceMode.RequireSpecific"/>. Must match an <see cref="ILlmTool.Name"/>
        /// in <see cref="Tools"/>. Ignored for other modes.
        /// </summary>
        public string RequiredToolName { get; set; } = "";

        /// <summary>
        /// When <see cref="Tools"/> is non-empty, streaming can either buffer the entire assistant
        /// iteration before emitting any <see cref="LlmStreamChunk.Text"/> (<c>true</c>), or use the default
        /// hybrid hold (<c>null</c>/<c>false</c>): stream only the prefix that cannot be part of an
        /// incomplete text-shaped tool JSON, then hold until balanced <c>{...}</c> closes. Full buffer is a
        /// compatibility escape hatch for exotic delta fragmentation.
        /// </summary>
        public bool? BufferFullStreamingIterationWhenToolsDeclared { get; set; }
    }

    /// <summary>
    /// Diagnostic record describing one tool call observed during an LLM turn.
    /// Captured by the tool-calling cycle (native FunctionCallContent or text-extracted JSON)
    /// regardless of streaming/non-streaming path, so the logging decorator can surface
    /// what tools the model actually invoked. Failure source is captured in <see cref="Success"/>.
    /// </summary>
    public readonly struct LlmToolCallTrace
    {
        /// <summary>Constructs a trace entry for one observed tool call.</summary>
        public LlmToolCallTrace(string name, bool success, double durationMs, string source)
        {
            Name = name ?? "";
            Success = success;
            DurationMs = durationMs;
            Source = source ?? "";
        }

        /// <summary>Tool name (matches <see cref="ILlmTool.Name"/>).</summary>
        public string Name { get; }

        /// <summary>True if the tool returned a non-error result (no <c>"success":false</c>) and did not throw.</summary>
        public bool Success { get; }

        /// <summary>Wall-clock execution time in milliseconds.</summary>
        public double DurationMs { get; }

        /// <summary>How this call was discovered: <c>native</c> (FunctionCallContent), <c>text</c> (extracted JSON), or <c>duplicate</c> / <c>missing</c>.</summary>
        public string Source { get; }
    }

    /// <summary>Model completion: text, error state, optional usage.</summary>
    public sealed class LlmCompletionResult
    {
        /// <summary>When <c>false</c>, inspect <see cref="Error"/>.</summary>
        public bool Ok { get; set; }

        /// <summary>Raw model text (or JSON command payload).</summary>
        public string Content { get; set; } = "";

        /// <summary>Failure or cancellation message.</summary>
        public string Error { get; set; } = "";

        /// <summary>Stable failure category for UI and retry policies.</summary>
        public LlmErrorCode ErrorCode { get; set; } = LlmErrorCode.None;

        /// <summary>HTTP status code when the failure came from an HTTP backend.</summary>
        public int? HttpStatus { get; set; }

        /// <summary>Provider hint for when the request may be retried.</summary>
        public int? RetryAfterSeconds { get; set; }

        /// <summary>Raw provider error body, intended for diagnostics only.</summary>
        public string ProviderErrorBody { get; set; } = "";

        /// <summary>Model identifier used by the provider when known.</summary>
        public string Model { get; set; } = "";

        /// <summary>Set from OpenAI-compatible HTTP when the payload includes <c>usage</c>.</summary>
        public int? PromptTokens { get; set; }

        /// <summary>Completion tokens from usage (HTTP).</summary>
        public int? CompletionTokens { get; set; }

        /// <summary>Total tokens from usage (HTTP).</summary>
        public int? TotalTokens { get; set; }

        /// <summary>
        /// Tool calls observed during this turn (native + text-extracted, in execution order).
        /// Empty when the model produced only text. Used by <see cref="LoggingLlmClientDecorator"/>
        /// to log <c>tools=[name(ok,12ms),name(fail,4ms)]</c>.
        /// </summary>
        public IReadOnlyList<LlmToolCallTrace> ExecutedToolCalls { get; set; } = Array.Empty<LlmToolCallTrace>();
    }

    /// <summary>One streaming chunk: text fragment and completion markers.</summary>
    public sealed class LlmStreamChunk
    {
        /// <summary>Text delta (may be empty on terminal chunks).</summary>
        public string Text { get; set; } = "";

        /// <summary><c>true</c> when the stream has finished.</summary>
        public bool IsDone { get; set; }

        /// <summary>Streaming failure text, if any.</summary>
        public string Error { get; set; }

        /// <summary>Stable failure category for UI and retry policies.</summary>
        public LlmErrorCode ErrorCode { get; set; } = LlmErrorCode.None;

        /// <summary>HTTP status code when the failure came from an HTTP backend.</summary>
        public int? HttpStatus { get; set; }

        /// <summary>Provider hint for when the request may be retried.</summary>
        public int? RetryAfterSeconds { get; set; }

        /// <summary>Model identifier used by the provider when known.</summary>
        public string Model { get; set; } = "";

        /// <summary>Usage fields: populated on the final chunk when the backend reports usage.</summary>
        public int? PromptTokens { get; set; }

        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }

        /// <summary>
        /// Tool calls executed in this streaming turn (final chunk only). Empty for intermediate
        /// chunks. Mirrors <see cref="LlmCompletionResult.ExecutedToolCalls"/> so the logging
        /// decorator can render the same diagnostic across stream and non-stream paths.
        /// </summary>
        public IReadOnlyList<LlmToolCallTrace> ExecutedToolCalls { get; set; } = Array.Empty<LlmToolCallTrace>();

        /// <summary>
        /// When <c>true</c> with <see cref="BufferedStreamingNoToolBinding"/>, host UI should show a short
        /// static line from chat config (tool invocation, text-shaped execute, hybrid JSON hold).
        /// </summary>
        public bool BufferedStreamingUseToolProgressHint { get; set; }

        /// <summary>
        /// Whether streaming text is buffered when tool declarations have no runtime binding.
        /// </summary>
        public bool BufferedStreamingNoToolBinding { get; set; }
    }

    /// <summary>
    /// Defines completion and streaming operations for LLM backends.
    /// </summary>
    public interface ILlmClient
    {
        /// <summary>Single completion; cancellation and timeouts are applied by outer decorators.</summary>
        Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>Attach tools for subsequent calls. Default implementation is no-op.</summary>
        virtual void SetTools(IReadOnlyList<ILlmTool> tools)
        {
        }

        /// <summary>
        /// Streaming completion: yields text chunks as they arrive.
        /// Default implementation falls back to <see cref="CompleteAsync"/> and emits one terminal chunk.
        /// </summary>
        /// <remarks>
        /// <b>Wrappers must override.</b> Any <see cref="ILlmClient"/> that decorates another client (logging, routing, timeout, retry)
        /// must override this member and delegate with <c>await foreach</c>. Otherwise the default DIM body collapses the stream into
        /// a single final chunk after <see cref="CompleteAsync"/> returns, so UI streaming appears to buffer entirely.
        /// </remarks>
        virtual async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            LlmCompletionResult result = await CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            if (result == null)
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = "null result",
                    ErrorCode = LlmErrorCode.ProviderError
                };
                yield break;
            }

            if (!result.Ok)
            {
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Error = result.Error,
                    ErrorCode = result.ErrorCode,
                    HttpStatus = result.HttpStatus,
                    RetryAfterSeconds = result.RetryAfterSeconds,
                    Model = result.Model
                };
                yield break;
            }

            yield return new LlmStreamChunk
            {
                Text = result.Content ?? "",
                IsDone = true,
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                TotalTokens = result.TotalTokens,
                Model = result.Model
            };
        }
    }
}
