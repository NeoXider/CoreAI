using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Authority;

namespace CoreAI.Ai
{
    /// <summary>
    /// AI task entry point: prompts, LLM invocation, memory, publishing <see cref="CoreAI.Messaging.ApplyAiGameCommand"/>.
    /// Typical production wiring layers <see cref="QueuedAiOrchestrator"/> above <see cref="AiOrchestrator"/>.
    /// </summary>
    public interface IAiOrchestrationService
    {
        /// <summary>Runs an AI task and returns the final textual result.</summary>
        Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default);

        /// <summary>
        /// Streaming variant of <see cref="RunTaskAsync"/> yielding model deltas as they arrive.
        /// Default DIM implementation buffers through <see cref="RunTaskAsync"/> and emits a single text chunk followed by termination.
        /// </summary>
        /// <remarks>
        /// Wrappers (queues, decorators, timeouts, authority) <b>must</b> override this method; otherwise streaming is silently reduced
        /// to one buffered text chunk.
        /// </remarks>
        virtual async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
            AiTaskRequest task,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            string content = await RunTaskAsync(task, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(content))
            {
                yield return new LlmStreamChunk { IsDone = true, Error = "empty result" };
                yield break;
            }

            yield return new LlmStreamChunk { Text = content };
            yield return new LlmStreamChunk { IsDone = true, Text = string.Empty };
        }

        /// <summary>
        /// Cancels in-flight and queued work for the logical cancellation scope in each role's current
        /// <see cref="AgentMemoryScope"/> partition (for example, a manual agent stop).
        /// </summary>
        /// <remarks>
        /// Implementations that support scoped multi-user queues must resolve identity with the role captured
        /// by each admitted item; the logical cancellation scope is not required to equal that role id.
        /// </remarks>
        void CancelTasks(string cancellationScope);
    }

    /// <summary>
    /// Resolves and captures the trusted actor before queue admission chooses durable and cancellation scopes.
    /// </summary>
    public interface IAiActorContextResolver
    {
        /// <summary>Resolves the actor for <paramref name="task"/> and stores it on the request.</summary>
        ActorContext ResolveActorContext(AiTaskRequest task);
    }

    /// <summary>
    /// Optional cancellation capability for multi-user hosts. It combines the logical cancellation
    /// scope with the current <see cref="AgentMemoryScope"/> resolved for the supplied role id, so stopping
    /// one learner's role cannot cancel the same role running for another learner.
    /// </summary>
    public interface IScopedAiTaskCancellation
    {
        /// <summary>Cancels work only in the current identity scope for <paramref name="roleId"/>.</summary>
        void CancelTasks(string cancellationScope, string roleId);
    }

    /// <summary>
    /// Optional lifecycle capability for wrappers that reject or cancel an admitted user turn before
    /// <see cref="IAiOrchestrationService.RunTaskAsync"/> or
    /// <see cref="IAiOrchestrationService.RunStreamingAsync"/> can enter the inner orchestrator.
    /// </summary>
    /// <remarks>
    /// The caller owns the one-shot guarantee and must invoke this capability at most once for one queued item.
    /// Implementations must suppress persistence failures so the original cancellation/rejection remains observable.
    /// This contract does not deduplicate a separate external retry of the same logical request.
    /// </remarks>
    internal interface IUnstartedAiTurnRecorder
    {
        /// <summary>Records the raw user turn for work that is guaranteed not to enter the inner orchestrator.</summary>
        void RecordUnstartedUserTurn(AiTaskRequest task);
    }
}
