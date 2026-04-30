using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Default context manager that keeps recent turns and creates a deterministic summary for older history.
    /// </summary>
    public sealed class DeterministicConversationContextManager : IAsyncConversationContextManager
    {
        private readonly IConversationSummaryStore _summaryStore;
        private readonly ITokenEstimator _estimator;

        /// <summary>
        /// Creates a new deterministic context manager.
        /// </summary>
        public DeterministicConversationContextManager(
            IConversationSummaryStore summaryStore,
            ITokenEstimator tokenEstimator = null)
        {
            _summaryStore = summaryStore ?? throw new ArgumentNullException(nameof(summaryStore));
            _estimator = tokenEstimator ?? new HeuristicTokenEstimator();
        }

        /// <inheritdoc />
        public ConversationContextSnapshot BuildSnapshot(
            string roleId,
            ChatMessage[] history,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            ConversationContextBuildArgs buildArgs = null)
        {
            if (history == null || history.Length == 0)
            {
                return new ConversationContextSnapshot();
            }

            int historyBudget = ConversationContextBudgetTokens.ResolveHistoryChatBudget(roleConfig, buildArgs);

            (int splitExclusive, List<ChatMessage> recent) =
                ConversationHistoryPartition.PartitionByBudget(history, _estimator, historyBudget);

            string storedSummary = _summaryStore.LoadSummary(roleId) ?? "";
            if (splitExclusive <= 0)
            {
                return new ConversationContextSnapshot
                {
                    Summary = storedSummary,
                    RecentMessages = recent.ToArray(),
                    WasCompacted = !string.IsNullOrWhiteSpace(storedSummary)
                };
            }

            string compactedSummary = ConversationBulletSummary.Format(storedSummary, history, splitExclusive);
            _summaryStore.SaveSummary(roleId, compactedSummary);

            return new ConversationContextSnapshot
            {
                Summary = compactedSummary,
                RecentMessages = recent.ToArray(),
                WasCompacted = true
            };
        }

        /// <inheritdoc />
        public Task<ConversationContextSnapshot> BuildSnapshotAsync(
            string roleId,
            ChatMessage[] history,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            ConversationContextBuildArgs buildArgs,
            string orchestrationTraceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(BuildSnapshot(roleId, history, roleConfig, buildArgs));
        }
    }
}
