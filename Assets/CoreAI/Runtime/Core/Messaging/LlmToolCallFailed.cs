namespace CoreAI.Messaging
{
    /// <summary>
    /// Published after a model-requested tool call fails or returns an unsuccessful result.
    /// </summary>
    public readonly struct LlmToolCallFailed
    {
        /// <summary>Creates an immutable tool-call failure event.</summary>
        public LlmToolCallFailed(
            string traceId,
            string roleId,
            string toolName,
            string argumentsJson,
            string error,
            double durationMs)
            : this(new LlmToolCallInfo(traceId, roleId, "", toolName, argumentsJson), error, durationMs)
        {
        }

        /// <summary>Creates an immutable tool-call failure event.</summary>
        public LlmToolCallFailed(
            LlmToolCallInfo info,
            string error,
            double durationMs)
        {
            Info = info;
            Error = error ?? "";
            DurationMs = durationMs;
        }

        /// <summary>Stable tool-call metadata.</summary>
        public LlmToolCallInfo Info { get; }

        /// <summary>Correlation id propagated from the LLM request.</summary>
        public string TraceId => Info.TraceId;

        /// <summary>Agent role that requested the tool.</summary>
        public string RoleId => Info.RoleId;

        /// <summary>Provider-level tool call id when available.</summary>
        public string CallId => Info.CallId;

        /// <summary>Tool name requested by the model.</summary>
        public string ToolName => Info.ToolName;

        /// <summary>Sanitized argument JSON, when enabled by settings.</summary>
        public string ArgumentsJson => Info.ArgumentsJson;

        /// <summary>Failure detail or unsuccessful tool result preview.</summary>
        public string Error { get; }

        /// <summary>Tool execution duration in milliseconds.</summary>
        public double DurationMs { get; }
    }
}
