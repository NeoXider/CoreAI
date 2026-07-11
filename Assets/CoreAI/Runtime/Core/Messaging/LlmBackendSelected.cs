using CoreAI.Ai;

namespace CoreAI.Messaging
{
    /// <summary>
    /// Published when the runtime resolves an LLM request to a concrete execution mode and routing profile.
    /// </summary>
    public readonly struct LlmBackendSelected
    {
        /// <summary>
        /// Creates an immutable backend selection event.
        /// </summary>
        public LlmBackendSelected(
            string traceId,
            string roleId,
            string routingProfileId,
            LlmExecutionMode executionMode,
            string clientType)
        {
            TraceId = traceId ?? "";
            RoleId = roleId ?? "";
            RoutingProfileId = routingProfileId ?? "";
            ExecutionMode = executionMode;
            ClientType = clientType ?? "";
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
        /// Runtime client type selected for the request.
        /// </summary>
        public string ClientType { get; }
    }
}
