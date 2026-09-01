using System;
using System.Threading;

namespace CoreAI.Ai
{
    /// <summary>Terminal classification for one LLM completion attempt.</summary>
    public enum AiLlmCompletionOutcome
    {
        /// <summary>The provider produced a usable completion.</summary>
        Succeeded = 0,

        /// <summary>The provider rejected the request or returned an error.</summary>
        ProviderFailure = 1,

        /// <summary>The operation was cancelled without a more specific attribution.</summary>
        Cancelled = 2,

        /// <summary>The operation was superseded by newer work in the same cancellation scope.</summary>
        Replaced = 3,

        /// <summary>The operation was cancelled because its caller-owned deadline elapsed.</summary>
        DeadlineCancellation = 4
    }

    /// <summary>IAiOrchestrationMetrics interface.</summary>
    public interface IAiOrchestrationMetrics
    {
        /// <summary>Records the actor, role, outcome, and duration of an LLM completion request.</summary>
        void RecordLlmCompletion(
            string actorId,
            string roleId,
            string traceId,
            AiLlmCompletionOutcome outcome,
            double wallMs);

        /// <summary>Records an actor's structured-response validation retry and its reason.</summary>
        void RecordStructuredRetry(string actorId, string roleId, string traceId, string reason);

        /// <summary>Records that an AI command was published for the given actor, role, and trace.</summary>
        void RecordCommandPublished(string actorId, string roleId, string traceId);
    }

    /// <summary>Mutable cancellation attribution owned by one queued orchestration operation.</summary>
    internal sealed class AiCancellationAttribution
    {
        private int _outcome = (int)AiLlmCompletionOutcome.Cancelled;

        internal void MarkReplaced()
        {
            Interlocked.Exchange(ref _outcome, (int)AiLlmCompletionOutcome.Replaced);
        }

        internal AiLlmCompletionOutcome Resolve(
            CancellationToken callerCancellationToken,
            CancellationToken deadlineCancellationToken)
        {
            AiLlmCompletionOutcome outcome =
                (AiLlmCompletionOutcome)Volatile.Read(ref _outcome);
            if (outcome == AiLlmCompletionOutcome.Replaced)
            {
                return outcome;
            }

            if (callerCancellationToken.IsCancellationRequested)
            {
                return AiLlmCompletionOutcome.Cancelled;
            }

            return deadlineCancellationToken.IsCancellationRequested
                ? AiLlmCompletionOutcome.DeadlineCancellation
                : AiLlmCompletionOutcome.Cancelled;
        }
    }

    /// <summary>Carries queue-owned cancellation attribution across asynchronous orchestration execution.</summary>
    internal static class AiCancellationAttributionContext
    {
        private static readonly AsyncLocal<Frame> Current = new();

        internal static IDisposable Push(AiCancellationAttribution attribution)
        {
            Frame previous = Current.Value;
            Frame frame = new(attribution);
            Current.Value = frame;
            return new Lease(frame, previous);
        }

        internal static AiLlmCompletionOutcome Resolve(
            CancellationToken callerCancellationToken,
            CancellationToken deadlineCancellationToken)
        {
            Frame frame = Current.Value;
            if (frame != null)
            {
                return frame.Attribution.Resolve(
                    callerCancellationToken,
                    deadlineCancellationToken);
            }

            if (callerCancellationToken.IsCancellationRequested)
            {
                return AiLlmCompletionOutcome.Cancelled;
            }

            return deadlineCancellationToken.IsCancellationRequested
                ? AiLlmCompletionOutcome.DeadlineCancellation
                : AiLlmCompletionOutcome.Cancelled;
        }

        private sealed class Frame
        {
            internal Frame(AiCancellationAttribution attribution)
            {
                Attribution = attribution ?? throw new ArgumentNullException(nameof(attribution));
            }

            internal AiCancellationAttribution Attribution { get; }
        }

        private sealed class Lease : IDisposable
        {
            private readonly Frame _owned;
            private readonly Frame _previous;
            private int _disposed;

            internal Lease(Frame owned, Frame previous)
            {
                _owned = owned;
                _previous = previous;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                if (ReferenceEquals(Current.Value, _owned))
                {
                    Current.Value = _previous;
                }
            }
        }
    }
}
