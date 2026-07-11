using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>No transcript capture.</summary>
    public sealed class NullConversationTranscriptStore : IConversationTranscriptStore
    {
        /// <inheritdoc />
        public void AppendTranscriptEntry(string roleId, ConversationEntry entry, bool persistToDisk = true)
        {
        }

        /// <inheritdoc />
        public IReadOnlyList<ConversationEntry> GetTranscriptEntries(string roleId, int maxEntries)
        {
            return System.Array.Empty<ConversationEntry>();
        }
    }
}
