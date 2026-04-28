namespace CoreAI.Ai
{
    /// <summary>
    /// Portable usage record for token accounting, quota, and cost estimation.
    /// </summary>
    public sealed class LlmUsageRecord
    {
        /// <summary>Request trace id.</summary>
        public string TraceId { get; set; } = "";

        /// <summary>Agent role id.</summary>
        public string RoleId { get; set; } = "";

        /// <summary>Routing profile id.</summary>
        public string RoutingProfileId { get; set; } = "";

        /// <summary>Execution mode used for this request.</summary>
        public LlmExecutionMode ExecutionMode { get; set; } = LlmExecutionMode.Auto;

        /// <summary>Provider or local model id.</summary>
        public string Model { get; set; } = "";

        /// <summary>Prompt/input token count.</summary>
        public int PromptTokens { get; set; }

        /// <summary>Completion/output token count.</summary>
        public int CompletionTokens { get; set; }

        /// <summary>Total token count.</summary>
        public int TotalTokens { get; set; }

        /// <summary>True when the request used streaming.</summary>
        public bool Streaming { get; set; }

        /// <summary>True when the request succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Adds usage counts from another record into this record.</summary>
        public void Add(LlmUsageRecord other)
        {
            if (other == null)
            {
                return;
            }

            PromptTokens += other.PromptTokens;
            CompletionTokens += other.CompletionTokens;
            TotalTokens += other.TotalTokens;
        }
    }
}
