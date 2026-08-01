namespace CoreAI.Ai
{
    /// <summary>
    /// Applies the same tenant/user/session/topic key boundary as agent memory and transcripts to compacted
    /// conversation summaries. The backing store retains its own locking and atomic-write semantics.
    /// </summary>
    public sealed class ScopedConversationSummaryStoreDecorator : IConversationSummaryStore
    {
        private readonly IConversationSummaryStore _inner;
        private readonly IAgentMemoryScopeProvider _scopeProvider;

        /// <summary>Creates a scoped summary facade over a host-provided backing store.</summary>
        public ScopedConversationSummaryStoreDecorator(
            IConversationSummaryStore inner,
            IAgentMemoryScopeProvider scopeProvider)
        {
            _inner = inner ?? new NullConversationSummaryStore();
            _scopeProvider = scopeProvider ?? new DefaultAgentMemoryScopeProvider();
        }

        /// <inheritdoc />
        public string LoadSummary(string roleId)
        {
            return _inner.LoadSummary(AgentMemoryScopeKey.Resolve(_scopeProvider, roleId));
        }

        /// <inheritdoc />
        public void SaveSummary(string roleId, string summary)
        {
            _inner.SaveSummary(AgentMemoryScopeKey.Resolve(_scopeProvider, roleId), summary);
        }

        /// <inheritdoc />
        public void ClearSummary(string roleId)
        {
            _inner.ClearSummary(AgentMemoryScopeKey.Resolve(_scopeProvider, roleId));
        }
    }
}
