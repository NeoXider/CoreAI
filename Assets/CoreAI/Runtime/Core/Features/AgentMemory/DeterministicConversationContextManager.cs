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

            history = PruneIfEnabled(history, buildArgs);
            if (history == null || history.Length == 0)
            {
                return new ConversationContextSnapshot();
            }

            string storedSummary = _summaryStore.LoadSummary(roleId) ?? "";
            int historyBudget = ConversationContextBudgetTokens.ResolveHistoryChatBudget(roleConfig, buildArgs);
            if (!ConversationContextBudgetTokens.ShouldPartitionForCompaction(
                    history,
                    _estimator,
                    historyBudget,
                    buildArgs))
            {
                return new ConversationContextSnapshot
                {
                    Summary = LimitSummaryIfNeeded(storedSummary, buildArgs),
                    RecentMessages = history,
                    WasCompacted = false
                };
            }

            (int splitExclusive, List<ChatMessage> recent) =
                ConversationHistoryPartition.PartitionByBudget(history, _estimator, historyBudget);

            if (splitExclusive <= 0)
            {
                string summaryOut = LimitSummaryIfNeeded(storedSummary, buildArgs);
                return new ConversationContextSnapshot
                {
                    Summary = summaryOut,
                    RecentMessages = recent.ToArray(),
                    WasCompacted = !string.IsNullOrWhiteSpace(summaryOut)
                };
            }

            int foldStart = ConversationBulletSummary.FindFoldStart(storedSummary, history, splitExclusive);
            string compactedSummary = LimitSummaryIfNeeded(
                ConversationBulletSummary.Format(storedSummary, history, splitExclusive, foldStart),
                buildArgs);
            ConversationContextSnapshot snapshot = new()
            {
                Summary = compactedSummary,
                RecentMessages = recent.ToArray(),
                WasCompacted = true
            };

            if (foldStart < splitExclusive)
            {
                if (buildArgs?.DeferSummaryPersistence == true)
                {
                    snapshot.CommitSummary = () => _summaryStore.SaveSummary(roleId, compactedSummary);
                }
                else
                {
                    _summaryStore.SaveSummary(roleId, compactedSummary);
                }
            }

            return snapshot;
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

        private string LimitSummaryIfNeeded(string summary, ConversationContextBuildArgs buildArgs)
        {
            int cap = buildArgs?.MaxRolledSummaryTokens ?? 0;
            if (cap <= 0)
            {
                return summary ?? "";
            }

            return ConversationRolledSummaryLimiter.Apply(summary, _estimator, cap);
        }

        private static ChatMessage[] PruneIfEnabled(ChatMessage[] history, ConversationContextBuildArgs buildArgs)
        {
            if (buildArgs == null || !buildArgs.EnableContextPruning)
            {
                return history;
            }

            return ConversationHistoryPruner.Prune(history, buildArgs.MaxRetainedToolResultMessages);
        }
    }
}
