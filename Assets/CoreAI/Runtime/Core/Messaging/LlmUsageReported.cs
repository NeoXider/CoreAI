using CoreAI.Ai;

namespace CoreAI.Messaging
{
    /// <summary>
    /// Published when an LLM backend reports token usage for a completed request or stream.
    /// </summary>
    public readonly struct LlmUsageReported
    {
        /// <summary>Preserves the original constructor signature for existing compiled hosts.</summary>
        public LlmUsageReported(string traceId, string roleId, string routingProfileId,
            LlmExecutionMode executionMode, string model, int? promptTokens, int? completionTokens,
            int? totalTokens, bool streaming, bool success, int? cacheReadTokens, int? cacheWriteTokens)
            : this(traceId, roleId, routingProfileId, executionMode, model, promptTokens, completionTokens,
                totalTokens, streaming, success, cacheReadTokens, cacheWriteTokens, null)
        {
        }

        /// <summary>
        /// Creates an immutable token usage event.
        /// </summary>
        public LlmUsageReported(
            string traceId,
            string roleId,
            string routingProfileId,
            LlmExecutionMode executionMode,
            string model,
            int? promptTokens,
            int? completionTokens,
            int? totalTokens,
            bool streaming,
            bool success,
            int? cacheReadTokens = null,
            int? cacheWriteTokens = null,
            int? reasoningTokens = null)
        {
            TraceId = traceId ?? "";
            RoleId = roleId ?? "";
            RoutingProfileId = routingProfileId ?? "";
            ExecutionMode = executionMode;
            Model = model ?? "";
            PromptTokens = promptTokens;
            CompletionTokens = completionTokens;
            TotalTokens = totalTokens;
            ReasoningTokens = reasoningTokens;
            CacheReadTokens = cacheReadTokens;
            CacheWriteTokens = cacheWriteTokens;
            Streaming = streaming;
            Success = success;
        }

        /// <summary>Correlation id propagated from the LLM request.</summary>
        public string TraceId { get; }

        /// <summary>Agent role used for routing.</summary>
        public string RoleId { get; }

        /// <summary>Resolved routing profile id or fallback label.</summary>
        public string RoutingProfileId { get; }

        /// <summary>Product-facing execution mode resolved for the request.</summary>
        public LlmExecutionMode ExecutionMode { get; }

        /// <summary>Provider-side model identifier when known.</summary>
        public string Model { get; }

        /// <summary>Prompt/input token count.</summary>
        public int? PromptTokens { get; }

        /// <summary>Completion/output token count.</summary>
        public int? CompletionTokens { get; }

        /// <summary>Total token count.</summary>
        public int? TotalTokens { get; }

        /// <summary>Provider-reported tokens spent on reasoning.</summary>
        public int? ReasoningTokens { get; }

        /// <summary>Prompt/input tokens read from a provider cache.</summary>
        public int? CacheReadTokens { get; }

        /// <summary>Prompt/input tokens written to a provider cache.</summary>
        public int? CacheWriteTokens { get; }

        /// <summary>Whether the request used streaming completion.</summary>
        public bool Streaming { get; }

        /// <summary>Whether the request completed successfully.</summary>
        public bool Success { get; }
    }
}
