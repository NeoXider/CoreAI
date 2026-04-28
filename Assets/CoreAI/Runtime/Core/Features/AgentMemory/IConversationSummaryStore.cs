namespace CoreAI.Ai
{
    /// <summary>
    /// Stores compact summaries for long-running conversations.
    /// </summary>
    public interface IConversationSummaryStore
    {
        /// <summary>Returns the stored summary for a role, or an empty string when no summary exists.</summary>
        string LoadSummary(string roleId);

        /// <summary>Saves a summary for a role.</summary>
        void SaveSummary(string roleId, string summary);

        /// <summary>Clears the stored summary for a role.</summary>
        void ClearSummary(string roleId);
    }
}
