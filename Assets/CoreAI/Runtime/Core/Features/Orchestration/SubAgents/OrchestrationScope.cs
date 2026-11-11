using System.Threading;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>Why a plan's scope was cancelled. The first reason wins.</summary>
    public enum OrchestrationStopReason
    {
        /// <summary>The scope is still live.</summary>
        None = 0,

        /// <summary>The player pressed Stop.</summary>
        PlayerStop = 1,

        /// <summary>The lead turn ended; any run still going is an orphan and is stopped.</summary>
        PlanCompleted = 2,

        /// <summary>The lead turn failed or was cancelled from outside.</summary>
        LeadFailed = 3,

        /// <summary>The session that owns the plan was disposed.</summary>
        SessionDisposed = 4
    }

    /// <summary>
    /// The one cancellation root of a plan. Every sub-agent run links its token to <see cref="Token"/>, so a
    /// single <see cref="Cancel"/> reaches the lead (when the host links the lead turn to it) and every
    /// running and waiting sub-agent at once, with no list of children to walk.
    /// <para>
    /// The source is cancelled but never disposed, the same choice <see cref="QueuedAiOrchestrator"/> makes
    /// for its lifetime source: a run still reading the token while the plan is torn down must not meet an
    /// <see cref="System.ObjectDisposedException"/>, and a timer-less source is reclaimed with the plan.
    /// </para>
    /// </summary>
    public sealed class OrchestrationScope
    {
        private readonly CancellationTokenSource _source = new();
        private readonly object _gate = new();
        private OrchestrationStopReason _reason;

        /// <summary>The token every run of the plan links to.</summary>
        public CancellationToken Token => _source.Token;

        /// <summary>True once <see cref="Cancel"/> has run.</summary>
        public bool IsCancellationRequested => _source.IsCancellationRequested;

        /// <summary>Why the scope was cancelled; <see cref="OrchestrationStopReason.None"/> while live.</summary>
        public OrchestrationStopReason StopReason
        {
            get
            {
                lock (_gate)
                {
                    return _reason;
                }
            }
        }

        /// <summary>
        /// Cancels the scope with <paramref name="reason"/>; returns false when it was already cancelled (the
        /// first reason is kept). Cancellation callbacks run synchronously on this call, outside any lock.
        /// </summary>
        public bool Cancel(OrchestrationStopReason reason)
        {
            lock (_gate)
            {
                if (_reason != OrchestrationStopReason.None)
                {
                    return false;
                }

                _reason = reason == OrchestrationStopReason.None ? OrchestrationStopReason.PlayerStop : reason;
            }

            _source.Cancel();
            return true;
        }
    }
}
