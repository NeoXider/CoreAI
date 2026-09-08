using System;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>
    /// Portable Task-based companion to <see cref="IConversationSummaryStore"/> for Load/Save/Clear.
    /// Built-in file, scoped, memory, and null stores implement it directly; the synchronous API stays
    /// available, but failed save/clear surface as exceptions instead of silent success. No Unity
    /// references are allowed in this contract: it must stay usable from portable hosts and tests.
    /// </summary>
    public interface IAsyncConversationSummaryStore
    {
        /// <summary>
        /// Returns the stored summary for a role, or an empty string when no summary exists. Strict
        /// implementations propagate corrupt or unreadable storage instead of silently overwriting it.
        /// </summary>
        Task<string> LoadSummaryAsync(string roleId, CancellationToken cancellationToken = default);

        /// <summary>Saves a summary for a role.</summary>
        Task SaveSummaryAsync(string roleId, string summary, CancellationToken cancellationToken = default);

        /// <summary>Clears the stored summary for a role.</summary>
        Task ClearSummaryAsync(string roleId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Explicit opt-in bridge for third-party synchronous summary backends that cannot implement
    /// <see cref="IAsyncConversationSummaryStore"/> themselves. When the wrapped store already speaks
    /// the async contract it is forwarded with no extra cost; otherwise the synchronous method runs
    /// inline on the calling thread, so file-backed backends pay blocking disk I/O on that thread.
    /// Production async paths must not silently infer this fallback: they reject sync-only backends
    /// with a clear error unless the host passes this adapter (or an equivalent explicit flag).
    /// </summary>
    public sealed class BlockingSyncSummaryStoreAsyncAdapter : IConversationSummaryStore, IAsyncConversationSummaryStore
    {
        private readonly IConversationSummaryStore _inner;

        /// <summary>Creates an explicit sync-to-async bridge over <paramref name="inner"/>.</summary>
        public BlockingSyncSummaryStoreAsyncAdapter(IConversationSummaryStore inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public string LoadSummary(string roleId) => _inner.LoadSummary(roleId);
        public void SaveSummary(string roleId, string summary) => _inner.SaveSummary(roleId, summary);
        public void ClearSummary(string roleId) => _inner.ClearSummary(roleId);

        /// <inheritdoc />
        public Task<string> LoadSummaryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            if (_inner is IAsyncConversationSummaryStore asyncInner)
            {
                return asyncInner.LoadSummaryAsync(roleId, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_inner.LoadSummary(roleId));
        }

        /// <inheritdoc />
        public Task SaveSummaryAsync(string roleId, string summary, CancellationToken cancellationToken = default)
        {
            if (_inner is IAsyncConversationSummaryStore asyncInner)
            {
                return asyncInner.SaveSummaryAsync(roleId, summary, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _inner.SaveSummary(roleId, summary);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task ClearSummaryAsync(string roleId, CancellationToken cancellationToken = default)
        {
            if (_inner is IAsyncConversationSummaryStore asyncInner)
            {
                return asyncInner.ClearSummaryAsync(roleId, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _inner.ClearSummary(roleId);
            return Task.CompletedTask;
        }
    }
}
