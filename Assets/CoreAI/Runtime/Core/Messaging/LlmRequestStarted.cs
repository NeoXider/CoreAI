using CoreAI.Ai;

namespace CoreAI.Messaging
{
    /// <summary>
    /// Published before a routed LLM request is delegated to the selected backend.
    /// </summary>
    public readonly struct LlmRequestStarted
    {
        /// <summary>
        /// Creates an immutable LLM request start event.
        /// </summary>
        public LlmRequestStarted(
            string traceId,
            string roleId,
            string routingProfileId,
            LlmExecutionMode executionMode,
            bool streaming)
        {
            TraceId = traceId ?? "";
            RoleId = roleId ?? "";
            RoutingProfileId = routingProfileId ?? "";
            ExecutionMode = executionMode;
            Streaming = streaming;
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
        /// Whether the request uses streaming completion.
        /// </summary>
        public bool Streaming { get; }
    }
}
