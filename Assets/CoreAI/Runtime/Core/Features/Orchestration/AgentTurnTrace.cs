namespace CoreAI.Ai
{
    /// <summary>
    /// Diagnostic trace for one agent turn.
    /// </summary>
    public sealed class AgentTurnTrace
    {
        /// <summary>Trace id.</summary>
        public string TraceId { get; set; } = "";

        /// <summary>Agent role id.</summary>
        public string RoleId { get; set; } = "";

        /// <summary>Routing profile id when known.</summary>
        public string RoutingProfileId { get; set; } = "";

        /// <summary>Model id when known.</summary>
        public string Model { get; set; } = "";

        /// <summary>System prompt preview.</summary>
        public string SystemPromptPreview { get; set; } = "";

        /// <summary>User payload.</summary>
        public string UserPayload { get; set; } = "";

        /// <summary>Assistant response.</summary>
        public string AssistantResponse { get; set; } = "";

        /// <summary>Error text, when the turn failed.</summary>
        public string Error { get; set; } = "";

        /// <summary>Prompt tokens.</summary>
        public int PromptTokens { get; set; }

        /// <summary>Completion tokens.</summary>
        public int CompletionTokens { get; set; }

        /// <summary>Total tokens.</summary>
        public int TotalTokens { get; set; }
    }
}
