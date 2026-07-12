using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;

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
            // WHY: The persisted summary carries a machine-only fold marker as its final line; every
            // snapshot-facing path must see only the clean prose.
            string cleanStoredSummary = ConversationFoldMarker.Strip(storedSummary);
            int historyBudget = ConversationContextBudgetTokens.ResolveHistoryChatBudget(roleConfig, buildArgs);
            if (!ConversationContextBudgetTokens.ShouldPartitionForCompaction(
                    history,
                    _estimator,
                    historyBudget,
                    buildArgs))
            {
                return new ConversationContextSnapshot
                {
                    Summary = LimitSummaryIfNeeded(cleanStoredSummary, buildArgs),
                    RecentMessages = history,
                    WasCompacted = false
                };
            }

            (int splitExclusive, List<ChatMessage> recent) =
                ConversationHistoryPartition.PartitionByBudget(history, _estimator, historyBudget);

            if (splitExclusive <= 0)
            {
                string summaryOut = LimitSummaryIfNeeded(cleanStoredSummary, buildArgs);
                return new ConversationContextSnapshot
                {
                    Summary = summaryOut,
                    RecentMessages = recent.ToArray(),
                    WasCompacted = !string.IsNullOrWhiteSpace(summaryOut)
                };
            }

            int foldStart = ConversationBulletSummary.FindFoldStart(
                storedSummary, history, splitExclusive, out ConversationFoldProbeResult probe);
            if (probe == ConversationFoldProbeResult.NoMatch)
            {
                // WHY: All persisted fold anchors vanished from history (heavy pruning/trimming); folding
                // from 0 once is the graceful floor, and the marker written below prevents any recurrence.
                Log.Instance.Warn(
                    $"[DeterministicConversationContextManager] Fold watermark not found in history for role '{roleId}'; re-folding entire prefix once.",
                    LogTag.Llm);
            }

            string compactedSummary = LimitSummaryIfNeeded(
                ConversationBulletSummary.Format(cleanStoredSummary, history, splitExclusive, foldStart),
                buildArgs);
            ConversationContextSnapshot snapshot = new()
            {
                Summary = compactedSummary,
                RecentMessages = recent.ToArray(),
                WasCompacted = true
            };

            if (foldStart < splitExclusive)
            {
                // WHY: The limiter runs BEFORE stamping so the fold marker (final line of the persisted
                // text) can never be trimmed away; the snapshot keeps the clean summary without the marker.
                string persistedSummary = ConversationFoldMarker.Stamp(compactedSummary, history, splitExclusive);
                if (buildArgs?.DeferSummaryPersistence == true)
                {
                    snapshot.CommitSummary = () => _summaryStore.SaveSummary(roleId, persistedSummary);
                }
                else
                {
                    _summaryStore.SaveSummary(roleId, persistedSummary);
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
