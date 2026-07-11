using System;

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
        /// </summary>
        public int MaxPending { get; set; } = 64;
    }

    /// <summary>
    /// Thrown by <see cref="QueuedAiOrchestrator.RunTaskAsync"/> when the pending queue is already at
    /// <see cref="AiOrchestrationQueueOptions.MaxPending"/> capacity.
    /// </summary>
    public sealed class AiOrchestrationQueueFullException : Exception
    {
        /// <summary>Creates the exception carrying the configured pending-queue capacity that was hit.</summary>
        public AiOrchestrationQueueFullException(int maxPending)
            : base($"AI orchestration queue is full (MaxPending={maxPending}); request rejected.")
        {
            MaxPending = maxPending;
        }

        /// <summary>The configured pending-queue capacity that was exceeded.</summary>
        public int MaxPending { get; }
    }
}
