using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Decorates an existing memory store and maps role ids to scoped keys.
    /// <para>
    /// This is also the only place where "clear the history" reaches the folded summary. A rolling
    /// summary is derived from the history: it lives in a separate <see cref="IConversationSummaryStore"/>,
    /// yet it exists only as a recap of THOSE messages that were in the history. The consumer calling
    /// <see cref="ClearChatHistory"/> (a new mission in RedoSchool, a "clear chat" button) does not know
    /// about the second store and must not have to; when it forgot about it, both context managers fed
    /// the previous lesson's summary into EVERY turn of the new one - before any compaction at all - and
    /// on the first compaction the old recap was merged into the new one.
    /// </para>
    /// </summary>
    public sealed class ScopedAgentMemoryStoreDecorator : IAgentMemoryStore, IAgentMemoryLoadDiagnostics,
        IAtomicAgentMemoryStore
    {
        private readonly IAgentMemoryStore _inner;
        private readonly IAgentMemoryScopeProvider _scopeProvider;
        private readonly IConversationSummaryStore _conversationSummaries;

        /// <summary>
        /// Creates a scoped memory store wrapper.
        /// </summary>
        /// <param name="inner">Backing store; keys it receives are already scoped.</param>
        /// <param name="scopeProvider">Scope provider shared with every other scoped decorator of this host.</param>
        /// <param name="conversationSummaries">
        /// The summary store registered in this same scope as <see cref="IConversationSummaryStore"/>
        /// (that is, already wrapped in <see cref="ScopedConversationSummaryStoreDecorator"/> with the
        /// same <paramref name="scopeProvider"/>): it is handed the raw role id, derives the key itself,
        /// and derives the same one. <c>null</c> means the host has no rolling summary and there is
        /// nothing to clear.
        /// </param>
        public ScopedAgentMemoryStoreDecorator(
            IAgentMemoryStore inner,
            IAgentMemoryScopeProvider scopeProvider,
            IConversationSummaryStore conversationSummaries = null)
        {
            _inner = inner ?? new NullAgentMemoryStore();
            _scopeProvider = scopeProvider ?? new DefaultAgentMemoryScopeProvider();
            _conversationSummaries = conversationSummaries;
        }

        /// <inheritdoc />
        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            return _inner.TryLoad(ToScopedKey(roleId), out state);
        }

        /// <inheritdoc />
        public AgentMemoryLoadStatus TryLoadDetailed(string roleId, out AgentMemoryState state)
        {
            string key = ToScopedKey(roleId);
            if (_inner is IAgentMemoryLoadDiagnostics diagnostics)
            {
                return diagnostics.TryLoadDetailed(key, out state);
            }

            // WHY: An inner store without the capability cannot tell "missing" from "unreadable"; report
            // the optimistic NotFound rather than blocking every first write behind a false Failed.
            return _inner.TryLoad(key, out state) && state != null
                ? AgentMemoryLoadStatus.Loaded
                : AgentMemoryLoadStatus.NotFound;
        }

        /// <inheritdoc />
        public void Save(string roleId, AgentMemoryState state)
        {
            _inner.Save(ToScopedKey(roleId), state);
        }

        /// <inheritdoc />
        public void Clear(string roleId)
        {
            _inner.Clear(ToScopedKey(roleId));
        }

        /// <inheritdoc />
        public void ClearChatHistory(string roleId)
        {
            _inner.ClearChatHistory(ToScopedKey(roleId));
            // WHY: A summary without the history it was folded from is not memory - it is somebody else's
            // lesson sitting in the prompt. Not one caller in the code has a "clear the history but keep
            // its recap" scenario (CoreAi.ClearContext, CoreAiChatService.ClearHistory,
            // AgentConfig.ClearMemory, the RedoSchool mission reset), so there is no opt-out flag either.
            _conversationSummaries?.ClearSummary(roleId);
        }

        /// <inheritdoc />
        public void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true)
        {
            _inner.AppendChatMessage(ToScopedKey(roleId), role, content, persistToDisk);
        }

        /// <inheritdoc />
        public ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0)
        {
            return _inner.GetChatHistory(ToScopedKey(roleId), maxMessages);
        }

        /// <inheritdoc />
        public Task<TResult> MutateAsync<TResult>(
            string roleId,
            Func<AgentMemoryState, TResult> mutator,
            CancellationToken cancellationToken = default)
        {
            // WHY: The extension delegates to an inner IAtomicAgentMemoryStore when available; otherwise it owns
            // a per-inner-store, per-scoped-key gate. Passing the scoped key here is essential: locking the raw
            // role id would serialize unrelated students and delegating the raw role would leak their state.
            return _inner.MutateAsync(ToScopedKey(roleId), mutator, cancellationToken);
        }

        private string ToScopedKey(string roleId)
        {
            return AgentMemoryScopeKey.Resolve(_scopeProvider, roleId);
        }
    }
}
