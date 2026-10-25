using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>Optional atomic read/compare-and-save capability for summary stores.</summary>
    public interface ITransactionalConversationSummaryStore
    {
        /// <summary>Captures content and a generation without holding a storage lock during compaction.</summary>
        ConversationSummaryTransaction OpenSummaryTransaction(string roleId);

        /// <summary>Captures content and a generation without blocking asynchronous storage.</summary>
        Task<ConversationSummaryTransaction> OpenSummaryTransactionAsync(string roleId, CancellationToken cancellationToken = default);
    }

    /// <summary>A generation-bound summary read; stale commits fail after any intervening save or reset.</summary>
    public sealed class ConversationSummaryTransaction : IDisposable
    {
        private readonly Func<string, bool> _commit;
        private readonly Func<string, CancellationToken, Task<bool>> _commitAsync;
        private IDisposable _lease;
        private bool _disposed;

        /// <summary>Creates a backend-owned compare-and-save binding. Dispose only after its commit has settled.</summary>
        public ConversationSummaryTransaction(string summary, Func<string, bool> commit,
            Func<string, CancellationToken, Task<bool>> commitAsync, IDisposable lease = null)
        {
            Summary = summary ?? "";
            _commit = commit;
            _commitAsync = commitAsync;
            _lease = lease;
        }

        /// <summary>The exact baseline content captured with this generation.</summary>
        public string Summary { get; }

        internal void Commit(string summary)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConversationSummaryTransaction));
            if (_commit == null) throw new NotSupportedException("This summary transaction requires asynchronous commit.");
            if (!_commit(summary))
            {
                Dispose();
                throw new InvalidOperationException("Summary changed or was cleared while context was being prepared; rebuild context before committing.");
            }
            Dispose();
        }

        internal async Task CommitAsync(string summary, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConversationSummaryTransaction));
            cancellationToken.ThrowIfCancellationRequested();
            if (_commitAsync == null) throw new NotSupportedException("This summary transaction requires synchronous commit.");
            if (!await _commitAsync(summary, cancellationToken))
            {
                Dispose();
                throw new InvalidOperationException("Summary changed or was cleared while context was being prepared; rebuild context before committing.");
            }
            Dispose();
        }

        /// <summary>Releases the captured generation when the prepared context is discarded.</summary>
        public void Dispose()
        {
            _disposed = true;
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
    }

    internal static class ConversationSummaryTransactions
    {
        internal static ConversationSummaryTransaction Open(IConversationSummaryStore store, string roleId)
        {
            if (store is ScopedConversationSummaryStoreDecorator scoped) store = scoped.Bind(roleId, out roleId);
            if (store is ITransactionalConversationSummaryStore transactional)
                return transactional.OpenSummaryTransaction(roleId);
            string key = roleId;
            return new ConversationSummaryTransaction(store.LoadSummary(key), summary =>
            {
                store.SaveSummary(key, summary);
                return true;
            }, null);
        }

        internal static async Task<ConversationSummaryTransaction> OpenAsync(IConversationSummaryStore store,
            string roleId, CancellationToken cancellationToken)
        {
            IAsyncConversationSummaryStore asyncStore = DeterministicConversationContextManager.RequireAsyncStore(store, ref roleId);
            if (asyncStore is ITransactionalConversationSummaryStore transactional)
                return await transactional.OpenSummaryTransactionAsync(roleId, cancellationToken);
            string key = roleId;
            string summary = await asyncStore.LoadSummaryAsync(key, cancellationToken);
            return new ConversationSummaryTransaction(summary, null, async (value, token) =>
            {
                await asyncStore.SaveSummaryAsync(key, value, token);
                return true;
            });
        }
    }
}
