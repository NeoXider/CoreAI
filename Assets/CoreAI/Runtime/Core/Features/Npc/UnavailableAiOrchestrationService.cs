using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// The orchestration service of a scope that has no model pipeline: every turn fails as
    /// <see cref="LlmErrorCode.BackendUnavailable"/>, so NPCs still speak their fallback lines instead of the scope
    /// failing to build. Never used when a real <see cref="IAiOrchestrationService"/> is registered.
    /// </summary>
    public sealed class UnavailableAiOrchestrationService : IAiOrchestrationService, IAiTaskResultService
    {
        /// <summary>Shared instance; the type has no state.</summary>
        public static readonly UnavailableAiOrchestrationService Instance = new();

        private const string Reason = "No AI orchestration service is registered in this scope.";

        /// <inheritdoc />
        public bool SupportsTaskResults => true;

        /// <inheritdoc />
        public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
        {
            return Task.FromException<string>(new LlmRequestFailedException(Reason,
                new LlmClientException(Reason, LlmErrorCode.BackendUnavailable)));
        }

        /// <inheritdoc />
        public Task<LlmCompletionResult> RunTaskResultAsync(AiTaskRequest task,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmCompletionResult
            {
                Ok = false,
                Error = Reason,
                ErrorCode = LlmErrorCode.BackendUnavailable
            });
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(AiTaskRequest task,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new LlmStreamChunk
            {
                IsDone = true,
                Error = Reason,
                ErrorCode = LlmErrorCode.BackendUnavailable
            };
        }

        /// <inheritdoc />
        public void CancelTasks(string cancellationScope)
        {
        }
    }
}
