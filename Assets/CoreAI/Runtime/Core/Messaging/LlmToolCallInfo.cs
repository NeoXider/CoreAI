namespace CoreAI.Messaging
{
    /// <summary>
    /// Stable identity and sanitized input metadata for a model-requested tool call.
    /// </summary>
    public readonly struct LlmToolCallInfo
    {
        /// <summary>Creates immutable tool-call metadata.</summary>
        public LlmToolCallInfo(string traceId, string roleId, string callId, string toolName, string argumentsJson)
        {
            TraceId = traceId ?? "";
            RoleId = roleId ?? "";
            CallId = callId ?? "";
            ToolName = toolName ?? "";
            ArgumentsJson = argumentsJson ?? "";
        }

        /// <summary>Correlation id propagated from the LLM request.</summary>
        public string TraceId { get; }

        /// <summary>Agent role that requested the tool.</summary>
        public string RoleId { get; }

        /// <summary>Provider-level tool call id when available.</summary>
        public string CallId { get; }

        /// <summary>Tool name requested by the model.</summary>
        public string ToolName { get; }

        /// <summary>Sanitized argument JSON, when enabled by settings.</summary>
        public string ArgumentsJson { get; }
    }
}
