namespace CoreAI.Messaging
{
    /// <summary>Optional portable telemetry DTO — Unity may bridge this to MessagePipe.</summary>
    public sealed class ConversationHistoryBudgetApplied
    {
        /// <summary>Request trace id.</summary>
        public string TraceId { get; set; } = "";

        /// <summary>Agent role.</summary>
        public string RoleId { get; set; } = "";

        /// <summary>Allocated history token estimate.</summary>
        public int HistoryTokenBudget { get; set; }

        /// <summary>Messages forwarded to MEAI chat history.</summary>
        public int ChatHistoryMessages { get; set; }

        /// <summary>Optional total context ceiling.</summary>
        public int ContextWindowCeiling { get; set; }
    }
}
