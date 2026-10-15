using CoreAI.Ai;

namespace CoreAI.Messaging
{
    /// <summary>
    /// Published before a routed LLM request is delegated to the selected backend.
    /// </summary>
    public readonly struct LlmRequestStarted
    {
        /// <summary>Preserves the original constructor signature for existing compiled hosts.</summary>
        public LlmRequestStarted(string traceId, string roleId, string routingProfileId,
            LlmExecutionMode executionMode, bool streaming, string actorId)
            : this(traceId, roleId, routingProfileId, executionMode, streaming, actorId,
                LlmReasoningEffort.Default, LlmReasoningDialect.Auto, null, null)
        {
        }

        /// <summary>
        /// Creates an immutable LLM request start event.
        /// </summary>
        public LlmRequestStarted(
            string traceId,
            string roleId,
            string routingProfileId,
            LlmExecutionMode executionMode,
            bool streaming,
            string actorId = "",
            LlmReasoningEffort reasoningEffort = LlmReasoningEffort.Default,
            LlmReasoningDialect reasoningDialect = LlmReasoningDialect.Auto,
            float? temperature = null,
            int? maxOutputTokens = null)
        {
            TraceId = traceId ?? "";
            RoleId = roleId ?? "";
            RoutingProfileId = routingProfileId ?? "";
            ExecutionMode = executionMode;
            Streaming = streaming;
            ActorId = actorId ?? "";
            ReasoningEffort = reasoningEffort;
            ReasoningDialect = reasoningDialect;
            Temperature = temperature;
            MaxOutputTokens = maxOutputTokens;
        }

        /// <summary>
        /// Correlation id propagated from the LLM request.
        /// </summary>
        public string TraceId { get; }

        /// <summary>
        /// Stable actor id admitted for this trace.
        /// </summary>
        public string ActorId { get; }

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

        /// <summary>Effective reasoning effort at request start.</summary>
        public LlmReasoningEffort ReasoningEffort { get; }

        /// <summary>Effective provider reasoning wire format.</summary>
        public LlmReasoningDialect ReasoningDialect { get; }

        /// <summary>Effective sampling temperature, when sent.</summary>
        public float? Temperature { get; }

        /// <summary>Effective output token cap, when sent.</summary>
        public int? MaxOutputTokens { get; }
    }
}
