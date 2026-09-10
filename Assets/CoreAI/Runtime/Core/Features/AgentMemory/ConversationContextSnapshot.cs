using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Result of preparing long-running conversation context for an LLM request.
    /// </summary>
    public sealed class ConversationContextSnapshot
    {
        private readonly SemaphoreSlim _commitGate = new(1, 1);
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

        internal System.Action CommitSummary { get; set; }
        internal Func<CancellationToken, Task> CommitSummaryAsync { get; set; }

        internal void Commit()
        {
            if (!_commitGate.Wait(0))
                throw new InvalidOperationException("Summary commit is already running; await CommitAsync instead.");
            try
            {
                if (CommitSummaryAsync != null)
                    throw new InvalidOperationException("This snapshot requires asynchronous durability confirmation; await CommitAsync.");
                CommitSummary?.Invoke();
                CommitSummary = null;
            }
            finally { _commitGate.Release(); }
        }

        internal async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await _commitGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (CommitSummary != null)
                    throw new InvalidOperationException("BuildSnapshotAsync is required for nonblocking asynchronous summary persistence.");
                if (CommitSummaryAsync == null) return;
                await CommitSummaryAsync(cancellationToken);
                // WHY: The store owns post-commit cancellation and durability; only acknowledged success consumes the callback.
                CommitSummaryAsync = null;
            }
            finally { _commitGate.Release(); }
        }
    }
}
