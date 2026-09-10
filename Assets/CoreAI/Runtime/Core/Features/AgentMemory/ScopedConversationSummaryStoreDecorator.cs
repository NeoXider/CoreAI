using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Applies the same tenant/user/session/topic key boundary as agent memory and transcripts to compacted
    /// conversation summaries. The backing store retains its own locking and atomic-write semantics. Scope
    /// resolution is synchronous and always happens before any await; the async path forwards to an
    /// async-capable inner store and never falls back to blocking sync I/O unless the host explicitly
    /// opts in with <paramref name="allowBlockingSyncFallback"/>.
    /// </summary>
    public sealed class ScopedConversationSummaryStoreDecorator : IConversationSummaryStore, IAsyncConversationSummaryStore
    {
        private readonly IConversationSummaryStore _inner;
        private readonly IAgentMemoryScopeProvider _scopeProvider;
        private readonly bool _allowBlockingSyncFallback;

        /// <summary>Creates a scoped summary facade over a host-provided backing store.</summary>
        /// <param name="allowBlockingSyncFallback">
        /// Explicit opt-in for sync-only custom backends: runs the inner synchronous method inline on the
        /// calling thread (blocking disk cost) when the inner store does not speak the async contract.
        /// Production async paths reject such backends with a clear error unless this is <c>true</c> or
        /// the backend is wrapped in <see cref="BlockingSyncSummaryStoreAsyncAdapter"/>.
        /// </param>
        public ScopedConversationSummaryStoreDecorator(
            IConversationSummaryStore inner,
            IAgentMemoryScopeProvider scopeProvider,
            bool allowBlockingSyncFallback = false)
        {
            _inner = inner ?? new NullConversationSummaryStore();
            _scopeProvider = scopeProvider ?? new DefaultAgentMemoryScopeProvider();
            _allowBlockingSyncFallback = allowBlockingSyncFallback;
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

        /// <inheritdoc />
        public Task<string> LoadSummaryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            string scoped = AgentMemoryScopeKey.Resolve(_scopeProvider, roleId);
            if (_inner is IAsyncConversationSummaryStore asyncInner)
            {
                return asyncInner.LoadSummaryAsync(scoped, cancellationToken);
            }

            if (!_allowBlockingSyncFallback)
            {
                throw new NotSupportedException(
                    "[ScopedConversationSummaryStore] Inner store does not implement " +
                    "IAsyncConversationSummaryStore; wrap it in BlockingSyncSummaryStoreAsyncAdapter or " +
                    "construct this decorator with allowBlockingSyncFallback: true.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_inner.LoadSummary(scoped));
        }

        /// <inheritdoc />
        public Task SaveSummaryAsync(string roleId, string summary, CancellationToken cancellationToken = default)
        {
            string scoped = AgentMemoryScopeKey.Resolve(_scopeProvider, roleId);
            if (_inner is IAsyncConversationSummaryStore asyncInner)
            {
                return asyncInner.SaveSummaryAsync(scoped, summary, cancellationToken);
            }

            if (!_allowBlockingSyncFallback)
            {
                throw new NotSupportedException(
                    "[ScopedConversationSummaryStore] Inner store does not implement " +
                    "IAsyncConversationSummaryStore; wrap it in BlockingSyncSummaryStoreAsyncAdapter or " +
                    "construct this decorator with allowBlockingSyncFallback: true.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _inner.SaveSummary(scoped, summary);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task ClearSummaryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            string scoped = AgentMemoryScopeKey.Resolve(_scopeProvider, roleId);
            if (_inner is IAsyncConversationSummaryStore asyncInner)
            {
                return asyncInner.ClearSummaryAsync(scoped, cancellationToken);
            }

            if (!_allowBlockingSyncFallback)
            {
                throw new NotSupportedException(
                    "[ScopedConversationSummaryStore] Inner store does not implement " +
                    "IAsyncConversationSummaryStore; wrap it in BlockingSyncSummaryStoreAsyncAdapter or " +
                    "construct this decorator with allowBlockingSyncFallback: true.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _inner.ClearSummary(scoped);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Captures one effective storage key for a complete async read/modify/write operation.
        /// Managers retain this binding across awaits and deferred commits; later host scope changes
        /// cannot move a summary into another user's partition.
        /// </summary>
        internal IAsyncConversationSummaryStore BindAsync(string roleId, out string boundRoleId)
        {
            boundRoleId = AgentMemoryScopeKey.Resolve(_scopeProvider, roleId);
            if (_inner is ScopedConversationSummaryStoreDecorator nested)
                return nested.BindAsync(boundRoleId, out boundRoleId);
            if (_inner is IAsyncConversationSummaryStore asyncInner) return asyncInner;
            if (_allowBlockingSyncFallback) return new BlockingSyncSummaryStoreAsyncAdapter(_inner);
            throw new NotSupportedException(
                "[ScopedConversationSummaryStore] Inner store does not implement IAsyncConversationSummaryStore; " +
                "wrap it in BlockingSyncSummaryStoreAsyncAdapter or explicitly enable blocking fallback.");
        }
    }
}
