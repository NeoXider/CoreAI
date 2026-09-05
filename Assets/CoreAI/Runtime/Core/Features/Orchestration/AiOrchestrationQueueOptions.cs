using System;
using CoreAI.Authority;

namespace CoreAI.Ai
{
    /// <summary>Options that control AI orchestration queue concurrency, ordering, and backpressure.</summary>
    public sealed class AiOrchestrationQueueOptions
    {
        /// <summary>Maximum number of AI tasks that may run concurrently.</summary>
        public int MaxConcurrent { get; set; } = 2;

        /// <summary>
        /// Maximum number of task + streaming requests allowed to wait in the pending queue at once.
        /// Enqueuing beyond this cap is rejected immediately (<see cref="AiOrchestrationQueueFullException"/>
        /// for <see cref="QueuedAiOrchestrator.RunTaskAsync"/>, a terminal error chunk for
        /// <see cref="QueuedAiOrchestrator.RunStreamingAsync"/>) instead of growing the queue unbounded.
        /// <para>
        /// The default suits a small session. It is a HARD REFUSAL, not backpressure: an actor over the
        /// cap is turned away before any work is attempted. Measured 2026-09-05 on the scale staircase —
        /// with 100 actors chatting on a synchronized burst, 64 pending refused 96 of 600 requests and
        /// the capacity gate failed; sized for the actor count the same run served 200 of 200 with none
        /// refused. Size it with <see cref="ForActorCount"/> rather than leaving the default when a host
        /// expects more than a handful of simultaneous actors.
        /// </para>
        /// </summary>
        public int MaxPending { get; set; } = 64;

        /// <summary>
        /// Queue options sized for a host that expects <paramref name="expectedActors"/> simultaneous
        /// actors, so a synchronized burst is queued rather than refused.
        /// </summary>
        /// <remarks>
        /// WHY these shapes: every actor may have one request in flight at the same instant (a burst is
        /// the normal case, not the tail), so the queue holds at least one slot per actor with headroom.
        /// Concurrency is what turns queue depth into latency — four lanes served all 200 actors but
        /// stretched the p95 to 5.2 s against a 5 s budget, while sixteen brought it to 1.35 s.
        /// Concurrency is still bounded by the real backend: raising it past what the provider can
        /// answer in parallel only moves the wait, it does not remove it.
        /// </remarks>
        /// <param name="expectedActors">Simultaneous actors the host expects; values below 1 are treated as 1.</param>
        public static AiOrchestrationQueueOptions ForActorCount(int expectedActors)
        {
            int actors = expectedActors < 1 ? 1 : expectedActors;
            return new AiOrchestrationQueueOptions
            {
                MaxPending = actors * 2 < 64 ? 64 : actors * 2,
                MaxConcurrent = Math.Max(2, Math.Min(16, (actors + 15) / 16))
            };
        }
    }

    /// <summary>
    /// Thrown by <see cref="QueuedAiOrchestrator.RunTaskAsync"/> when the pending queue is already at
    /// <see cref="AiOrchestrationQueueOptions.MaxPending"/> capacity.
    /// </summary>
    public sealed class AiOrchestrationQueueFullException : Exception
    {
        /// <summary>Creates the exception carrying the configured pending-queue capacity that was hit.</summary>
        public AiOrchestrationQueueFullException(int maxPending)
            : this(LocalActorIdentityProvider.DefaultActorId, maxPending)
        {
        }

        /// <summary>Creates an actor-specific rejection for the configured pending-queue capacity.</summary>
        public AiOrchestrationQueueFullException(string actorId, int maxPending)
            : base(
                $"AI orchestration request for actor '{NormalizeActorId(actorId)}' rejected: " +
                $"pending capacity is exhausted (MaxPending={maxPending}).")
        {
            ActorId = NormalizeActorId(actorId);
            MaxPending = maxPending;
        }

        /// <summary>The actor whose request was rejected.</summary>
        public string ActorId { get; }

        /// <summary>The configured pending-queue capacity that was exceeded.</summary>
        public int MaxPending { get; }

        private static string NormalizeActorId(string actorId)
        {
            return string.IsNullOrWhiteSpace(actorId)
                ? LocalActorIdentityProvider.DefaultActorId
                : actorId.Trim();
        }
    }
}
