using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Result of preparing long-running conversation context for an LLM request.
    /// </summary>
    public sealed class ConversationContextSnapshot : IDisposable
    {
        private readonly SemaphoreSlim _commitGate = new(1, 1);
        private readonly object _lifetimeGate = new();
        private IDisposable _ownedSummaryLease;
        private int _activeCommits;
        private bool _disposed;
        /// <summary>Summary of older messages that were compacted out of the live chat window.</summary>
        public string Summary { get; set; } = "";

        /// <summary>Recent messages that should still be sent as chat history.</summary>
        public ChatMessage[] RecentMessages { get; set; } = System.Array.Empty<ChatMessage>();

        /// <summary>True when older history was compacted into <see cref="Summary"/>.</summary>
        public bool WasCompacted { get; set; }

        /// <summary>
        /// Estimated tokens of summary prose dropped — by the manager's explicit cap, and by the
        /// orchestrator when the copy it SENDS had to fit the request reserve (<see cref="Summary"/>
        /// itself keeps the stored text);
        /// zero when the whole summary was emitted.
        /// </summary>
        public int SummaryTokensDropped { get; set; }

        /// <summary>
        /// Messages of the recent tail that context pruning removed (superseded tool results, exact duplicates).
        /// Kept apart from messages the window or budget left out, so a routine prune is not reported as a loss.
        /// </summary>
        public int PrunedMessageCount { get; set; }

        /// <summary>
        /// Messages older than the recent tail that the compactor did not receive this turn (they did not fit its
        /// payload): neither sent verbatim nor retold by the summary yet; the next compaction folds them.
        /// </summary>
        public int DeferredFoldMessageCount { get; set; }

        internal System.Action CommitSummary { get; set; }
        internal Func<CancellationToken, Task> CommitSummaryAsync { get; set; }

        internal void OwnSummaryLease(IDisposable lease)
        {
            lock (_lifetimeGate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ConversationContextSnapshot));
                if (_ownedSummaryLease != null) throw new InvalidOperationException("The snapshot already owns a summary generation.");
                _ownedSummaryLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }
        }

        internal void Commit()
        {
            EnterCommit();
            bool acquired = false;
            try
            {
                acquired = _commitGate.Wait(0);
                if (!acquired)
                    throw new InvalidOperationException("Summary commit is already running; await CommitAsync instead.");
                ThrowIfDisposed();
                if (CommitSummaryAsync != null)
                    throw new InvalidOperationException("This snapshot requires asynchronous durability confirmation; await CommitAsync.");
                CommitSummary?.Invoke();
                CommitSummary = null;
            }
            finally
            {
                if (acquired) _commitGate.Release();
                ExitCommit();
            }
        }

        internal async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            EnterCommit();
            bool acquired = false;
            try
            {
                await _commitGate.WaitAsync(cancellationToken);
                acquired = true;
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                if (CommitSummary != null)
                    throw new InvalidOperationException("BuildSnapshotAsync is required for nonblocking asynchronous summary persistence.");
                if (CommitSummaryAsync == null) return;
                await CommitSummaryAsync(cancellationToken);
                // WHY: The store owns post-commit cancellation and durability; only acknowledged success consumes the callback.
                CommitSummaryAsync = null;
            }
            finally
            {
                if (acquired) _commitGate.Release();
                ExitCommit();
            }
        }

        private void EnterCommit()
        {
            lock (_lifetimeGate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ConversationContextSnapshot));
                _activeCommits++;
            }
        }

        private void ThrowIfDisposed()
        {
            lock (_lifetimeGate)
                if (_disposed) throw new ObjectDisposedException(nameof(ConversationContextSnapshot));
        }

        private void ExitCommit()
        {
            IDisposable lease = null;
            lock (_lifetimeGate)
            {
                _activeCommits--;
                if (_disposed && _activeCommits == 0) lease = ReleaseOwnedState();
            }
            lease?.Dispose();
        }

        private IDisposable ReleaseOwnedState()
        {
            IDisposable lease = _ownedSummaryLease;
            _ownedSummaryLease = null;
            CommitSummary = null;
            CommitSummaryAsync = null;
            return lease;
        }

        /// <summary>
        /// Discards uncommitted summary state without changing projection properties. An admitted commit
        /// may finish; its generation lease is released only after all admitted commits settle.
        /// </summary>
        public void Dispose()
        {
            IDisposable lease = null;
            lock (_lifetimeGate)
            {
                _disposed = true;
                if (_activeCommits == 0) lease = ReleaseOwnedState();
            }
            lease?.Dispose();
        }
    }
}
