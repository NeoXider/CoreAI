namespace CoreAI.Messaging
{
    /// <summary>
    /// In-memory record of one tool-call lifecycle event for tests and diagnostics.
    /// </summary>
    public sealed class LlmToolCallRecord
    {
        /// <summary>Stable tool-call metadata.</summary>
        public LlmToolCallInfo Info { get; set; }

        /// <summary>Lifecycle status: started, completed, or failed.</summary>
        public string Status { get; set; } = "";

        /// <summary>Sanitized result preview for completed calls.</summary>
        public string ResultJson { get; set; } = "";

        /// <summary>Error message or unsuccessful result preview.</summary>
        public string Error { get; set; } = "";

        /// <summary>Execution duration in milliseconds.</summary>
        public double DurationMs { get; set; }
    }
}
