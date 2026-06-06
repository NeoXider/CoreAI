using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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

        /// <summary>Cancels in-flight and queued work for the given cancellation scope (e.g. manual agent stop).</summary>
        void CancelTasks(string cancellationScope);
    }
}