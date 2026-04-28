namespace CoreAI.Ai
{
    /// <summary>
    /// Empty summary store used when an application does not persist compacted conversation summaries.
    /// </summary>
    public sealed class NullConversationSummaryStore : IConversationSummaryStore
    {
        /// <inheritdoc />
        public string LoadSummary(string roleId) => "";

        /// <inheritdoc />
        public void SaveSummary(string roleId, string summary)
        {
        }

        /// <inheritdoc />
        public void ClearSummary(string roleId)
        {
        }
    }
}
