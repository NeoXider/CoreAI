using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Applies the canonical tenant/user/session/topic key boundary to structured conversation transcripts.
    /// Kept separate from <see cref="ScopedAgentMemoryStoreDecorator"/> so a memory-only backing store does not
    /// falsely advertise the optional <see cref="IConversationTranscriptStore"/> capability.
    /// </summary>
    public sealed class ScopedConversationTranscriptStoreDecorator : IConversationTranscriptStore
    {
        private readonly IConversationTranscriptStore _inner;
        private readonly IAgentMemoryScopeProvider _scopeProvider;

        /// <summary>Creates a scoped transcript facade over a transcript-capable backing store.</summary>
        public ScopedConversationTranscriptStoreDecorator(
            IConversationTranscriptStore inner,
            IAgentMemoryScopeProvider scopeProvider)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _scopeProvider = scopeProvider ?? new DefaultAgentMemoryScopeProvider();
        }

        /// <inheritdoc />
        public void AppendTranscriptEntry(string roleId, ConversationEntry entry, bool persistToDisk = true)
        {
            _inner.AppendTranscriptEntry(AgentMemoryScopeKey.Resolve(_scopeProvider, roleId), entry, persistToDisk);
        }

        /// <inheritdoc />
        public IReadOnlyList<ConversationEntry> GetTranscriptEntries(string roleId, int maxEntries)
        {
            return _inner.GetTranscriptEntries(AgentMemoryScopeKey.Resolve(_scopeProvider, roleId), maxEntries);
        }
    }
}
