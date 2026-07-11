namespace CoreAI.Messaging
{
    /// <summary>Reports how chat history was trimmed to fit a context budget.</summary>
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
