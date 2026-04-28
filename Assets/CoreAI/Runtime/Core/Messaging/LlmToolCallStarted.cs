namespace CoreAI.Messaging
{
    /// <summary>
    /// Published immediately before a model-requested tool call is executed.
    /// </summary>
    public readonly struct LlmToolCallStarted
    {
        /// <summary>Creates an immutable tool-call start event.</summary>
        public LlmToolCallStarted(string traceId, string roleId, string toolName, string argumentsJson)
            : this(new LlmToolCallInfo(traceId, roleId, "", toolName, argumentsJson))
        {
        }

        /// <summary>Creates an immutable tool-call start event.</summary>
        public LlmToolCallStarted(LlmToolCallInfo info)
        {
            Info = info;
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
    }
}
