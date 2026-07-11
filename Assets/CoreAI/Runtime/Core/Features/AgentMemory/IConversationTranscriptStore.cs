using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>Optional structured transcript in addition to flat <see cref="IAgentMemoryStore"/> chat.</summary>
    public interface IConversationTranscriptStore
    {
        /// <summary>Adds an entry after user/assistant lines or MEAI events.</summary>
        void AppendTranscriptEntry(string roleId, ConversationEntry entry, bool persistToDisk = true);

        /// <summary>Returns the last <paramref name="maxEntries"/> transcript rows for the role.</summary>
        IReadOnlyList<ConversationEntry> GetTranscriptEntries(string roleId, int maxEntries);
    }
}
