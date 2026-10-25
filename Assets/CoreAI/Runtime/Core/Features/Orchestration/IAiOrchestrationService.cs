using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Authority;

namespace CoreAI.Ai
{
    /// <summary>
    /// Optional truthful completion capability. A legacy string result does not prove success and
    /// must never be promoted to a successful completion by consumers of this contract.
    /// </summary>
    public interface IAiTaskResultService
    {
        /// <summary>Whether the complete decorated execution path exposes truthful task results.</summary>
        bool SupportsTaskResults => true;

        /// <summary>Runs the same task pipeline while retaining completion metadata and failure status.</summary>
        Task<LlmCompletionResult> RunTaskResultAsync(AiTaskRequest task, CancellationToken cancellationToken = default);
    }

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
            if (this is IAiTaskResultService typed && typed.SupportsTaskResults)
            {
                LlmCompletionResult result = await typed.RunTaskResultAsync(task, cancellationToken);
                yield return new LlmStreamChunk
                {
                    IsDone = true,
                    Text = result?.Content ?? "",
                    Error = result?.Ok == true ? null : result?.Error ?? "No typed completion was returned.",
                    ErrorCode = result?.Ok == true ? LlmErrorCode.None :
                        result?.ErrorCode is LlmErrorCode code && code != LlmErrorCode.None ? code : LlmErrorCode.ProviderError,
                    HttpStatus = result?.HttpStatus,
                    RetryAfterSeconds = result?.RetryAfterSeconds,
                    Model = result?.Model ?? "",
                    PromptTokens = result?.PromptTokens,
                    CompletionTokens = result?.CompletionTokens,
                    ReasoningTokens = result?.ReasoningTokens,
                    TotalTokens = result?.TotalTokens,
                    LastRoundtripPromptTokens = result?.LastRoundtripPromptTokens,
                    CacheReadTokens = result?.CacheReadTokens ?? 0,
                    CacheWriteTokens = result?.CacheWriteTokens ?? 0,
                    ExecutedToolCalls = result?.ExecutedToolCalls ?? System.Array.Empty<LlmToolCallTrace>()
                };
                yield break;
            }
            string content = await RunTaskAsync(task, cancellationToken);
            if (string.IsNullOrEmpty(content))
            {
                yield return new LlmStreamChunk { IsDone = true, Error = "empty result" };
                yield break;
            }

            yield return new LlmStreamChunk { Text = content };
            yield return new LlmStreamChunk { IsDone = true, Text = string.Empty };
        }

        /// <summary>
        /// Cancels admitted actor-aware work globally for the logical scope or connection-session alias.
        /// Actorless work is selected within each role's current <see cref="AgentMemoryScope"/> partition.
        /// Use <see cref="IActorAiTaskCancellation"/> to stop only one actor connection.
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

    /// <summary>Optional cancellation capability that stops every queued or running turn for a role.</summary>
    public interface IRoleAiTaskCancellation
    {
        /// <summary>Cancels work for the role across actor sessions and memory partitions.</summary>
        void CancelRoleTasks(string roleId);
    }

    /// <summary>Optional connection cancellation capability for hosts supplying trusted actor contexts.</summary>
    public interface IActorAiTaskCancellation
    {
        /// <summary>
        /// Cancels every queued or running request matching the actor id and connection session id,
        /// optionally restricted to one role. Logical cancellation scopes may be empty.
        /// </summary>
        void CancelActorTasks(ActorContext actorContext, string roleId = null);
    }

    /// <summary>
    /// Optional role-filtered logical cancellation capability. Actor-aware logical scopes are global
    /// admin selections; actorless work uses the role's current <see cref="AgentMemoryScope"/> partition.
    /// Player/session callers use <see cref="IActorAiTaskCancellation"/> for connection isolation.
    /// </summary>
    public interface IScopedAiTaskCancellation
    {
        /// <summary>Cancels matching logical work for <paramref name="roleId"/> using the service's admission model.</summary>
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
