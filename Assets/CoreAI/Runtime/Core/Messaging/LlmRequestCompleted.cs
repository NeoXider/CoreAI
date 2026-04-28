using CoreAI.Ai;

namespace CoreAI.Messaging
{
    /// <summary>
    /// Published after a routed LLM request completes, fails, or is cancelled.
    /// </summary>
    public readonly struct LlmRequestCompleted
    {
        /// <summary>
        /// Creates an immutable LLM request completion event.
        /// </summary>
        public LlmRequestCompleted(
            string traceId,
            string roleId,
            string routingProfileId,
            LlmExecutionMode executionMode,
            bool streaming,
            bool success,
            string error,
            LlmErrorCode errorCode = LlmErrorCode.None)
        {
            TraceId = traceId ?? "";
            RoleId = roleId ?? "";
            RoutingProfileId = routingProfileId ?? "";
            ExecutionMode = executionMode;
            Streaming = streaming;
            Success = success;
            Error = error ?? "";
            ErrorCode = errorCode;
        }

        /// <summary>
        /// Correlation id propagated from the LLM request.
        /// </summary>
        public string TraceId { get; }

        /// <summary>
        /// Agent role used for routing.
        /// </summary>
        public string RoleId { get; }

        /// <summary>
        /// Resolved routing profile id or fallback label.
        /// </summary>
        public string RoutingProfileId { get; }

        /// <summary>
        /// Product-facing execution mode resolved for the request.
        /// </summary>
        public LlmExecutionMode ExecutionMode { get; }

        /// <summary>
        /// Whether the request used streaming completion.
        /// </summary>
        public bool Streaming { get; }

        /// <summary>
        /// True when the backend returned a successful response or stream.
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Backend or routing error when the request did not complete successfully.
        /// </summary>
        public string Error { get; }

        /// <summary>
        /// Stable backend or routing error category.
        /// </summary>
        public LlmErrorCode ErrorCode { get; }
    }
}
