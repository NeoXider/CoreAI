namespace CoreAI.Messaging
{
    /// <summary>
    /// Published after a model-requested tool call finishes successfully.
    /// </summary>
    public readonly struct LlmToolCallCompleted
    {
        /// <summary>Creates an immutable tool-call completion event.</summary>
        public LlmToolCallCompleted(
            string traceId,
            string roleId,
            string toolName,
            string argumentsJson,
            string resultJson,
            double durationMs)
            : this(new LlmToolCallInfo(traceId, roleId, "", toolName, argumentsJson), resultJson, durationMs)
        {
        }

        /// <summary>Creates an immutable tool-call completion event.</summary>
        public LlmToolCallCompleted(
            LlmToolCallInfo info,
            string resultJson,
            double durationMs)
        {
            Info = info;
            ResultJson = resultJson ?? "";
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

        /// <summary>Sanitized result JSON or preview, when enabled by settings.</summary>
        public string ResultJson { get; }

        /// <summary>Tool execution duration in milliseconds.</summary>
        public double DurationMs { get; }
    }
}
