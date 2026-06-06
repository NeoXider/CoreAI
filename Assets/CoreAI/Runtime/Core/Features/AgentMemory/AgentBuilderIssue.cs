namespace CoreAI.Ai
{
    /// <summary>
    /// Categorises validation issues surfaced by <see cref="AgentBuilder.ValidateOnBuild"/>.
    /// </summary>
    public enum AgentBuilderIssueCode
    {
        /// <summary>No system prompt and no built-in fallback for the role.</summary>
        MissingSystemPrompt,

        /// <summary><see cref="AgentMode.ToolsAndChat"/> or <see cref="AgentMode.ToolsOnly"/> with zero registered tools.</summary>
        NoToolsForToolMode,

        /// <summary><c>WithLlmContextCompaction(true)</c> was requested but the global gate is off.</summary>
        CompactionGateDisabled,

        /// <summary><c>WithChatHistory(...)</c> was enabled with a non-positive message cap.</summary>
        InvalidChatHistorySize,

        /// <summary><c>WithTemperature(...)</c> was configured outside the supported sampling range.</summary>
        TemperatureOutOfRange
    }

    /// <summary>
    /// Single non-fatal validation issue produced by <see cref="AgentBuilder.Build"/>.
    /// Used by <see cref="AgentBuilder.ValidateOnBuild"/> for editor tooling and tests.
    /// </summary>
    public readonly struct AgentBuilderIssue
    {
        public AgentBuilderIssue(AgentBuilderIssueCode code, string message)
        {
            Code = code;
            Message = message;
        }

        public AgentBuilderIssueCode Code { get; }
        public string Message { get; }
    }
}