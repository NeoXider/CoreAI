using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Delegates to <see cref="LlmAssistedConversationContextManager"/> when
    /// <see cref="ConversationContextBuildArgs.UseLlmContextCompaction"/> is true (and an LLM client was supplied at construction);
    /// otherwise uses <see cref="DeterministicConversationContextManager"/> for async and sync paths.
    /// </summary>
    public sealed class SelectingConversationContextManager : IAsyncConversationContextManager
    {
        private readonly DeterministicConversationContextManager _deterministic;
        private readonly LlmAssistedConversationContextManager _llmAssisted;

        /// <summary>Creates a manager that can switch between deterministic and LLM-assisted compaction per request.</summary>
        public SelectingConversationContextManager(
            IConversationSummaryStore summaryStore,
            ITokenEstimator tokenEstimator,
            ILlmClient compactionLlmClient,
            LlmContextCompactionOptions options = null)
        {
            ITokenEstimator estimator = tokenEstimator ?? new HeuristicTokenEstimator();
            _deterministic = new DeterministicConversationContextManager(summaryStore, estimator);
            _llmAssisted = new LlmAssistedConversationContextManager(
                summaryStore,
                estimator,
                compactionLlmClient ?? throw new ArgumentNullException(nameof(compactionLlmClient)),
                options ?? LlmContextCompactionOptions.Default());
        }

        /// <inheritdoc />
        public ConversationContextSnapshot BuildSnapshot(
            string roleId,
            ChatMessage[] history,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            ConversationContextBuildArgs buildArgs = null)
        {
            return _deterministic.BuildSnapshot(roleId, history, roleConfig, buildArgs);
        }

        /// <inheritdoc />
        public async Task<ConversationContextSnapshot> BuildSnapshotAsync(
            string roleId,
            ChatMessage[] history,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            ConversationContextBuildArgs buildArgs,
            string orchestrationTraceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool useLlm = buildArgs != null && buildArgs.UseLlmContextCompaction;
            if (useLlm)
            {
                return await _llmAssisted
                    .BuildSnapshotAsync(roleId, history, roleConfig, buildArgs, orchestrationTraceId, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await _deterministic
                .BuildSnapshotAsync(roleId, history, roleConfig, buildArgs, orchestrationTraceId, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}