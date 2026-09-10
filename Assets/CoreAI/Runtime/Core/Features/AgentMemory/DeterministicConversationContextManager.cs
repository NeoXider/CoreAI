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

            ConversationContextSnapshot snapshot = ProjectSnapshot(roleId, history, roleConfig, buildArgs,
                _summaryStore.LoadSummary(roleId) ?? "", out string persistedSummary);
            if (persistedSummary != null)
            {
                snapshot.CommitSummary = () => _summaryStore.SaveSummary(roleId, persistedSummary);
                if (buildArgs?.DeferSummaryPersistence != true) snapshot.Commit();
            }
            return snapshot;
        }

        private ConversationContextSnapshot ProjectSnapshot(string roleId, ChatMessage[] history,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig, ConversationContextBuildArgs buildArgs,
            string storedSummary, out string persistedSummary)
        {
            persistedSummary = null;

            // WHY: Compaction folds the old prefix into the durable rolling summary, so it must see
            // the FULL history. Pruning first would drop superseded tool results before they are
            // summarized and they would vanish from every future prompt without a trace. Pruning
            // still applies, but only to the emitted recent tail (prompt-level noise control).
            // WHY: The persisted summary carries a machine-only fold marker as its final line; every
            // snapshot-facing path must see only the clean prose.
            string cleanStoredSummary = ConversationFoldMarker.Strip(storedSummary);
            // WHY: The recent tail is bounded by its own budget whether or not a summary exists; the
            // summary reserve is never lent to the tail, so an explicit recent-tail override keeps its
            // meaning. The reserve itself is not applied here either: it bounds the request copy of the
            // summary in the orchestrator, never the text this manager persists (see ResolveSummaryTokenCap).
            int historyBudget = ConversationContextBudgetTokens.ResolveHistoryChatBudget(roleConfig, buildArgs);
            if (history.Length <= ResolveMessageLimit(roleConfig) &&
                !ConversationContextBudgetTokens.ShouldPartitionForCompaction(
                    history,
                    _estimator,
                    historyBudget,
                    buildArgs))
            {
                string storedOut = LimitSummaryToBudget(cleanStoredSummary, buildArgs, out int storedDropped);
                return new ConversationContextSnapshot
                {
                    Summary = storedOut,
                    RecentMessages = PruneIfEnabled(history, buildArgs),
                    WasCompacted = false,
                    SummaryTokensDropped = storedDropped
                };
            }

            (int splitExclusive, List<ChatMessage> recent) =
                PartitionHistory(history, _estimator, historyBudget, roleConfig);

            if (splitExclusive <= 0)
            {
                string summaryOut = LimitSummaryToBudget(cleanStoredSummary, buildArgs, out int summaryDropped);
                return new ConversationContextSnapshot
                {
                    Summary = summaryOut,
                    RecentMessages = PruneIfEnabled(recent.ToArray(), buildArgs),
                    WasCompacted = !string.IsNullOrWhiteSpace(summaryOut),
                    SummaryTokensDropped = summaryDropped
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

            string compactedSummary = LimitSummaryToBudget(
                ConversationBulletSummary.Format(cleanStoredSummary, history, splitExclusive, foldStart),
                buildArgs,
                out int compactedDropped);
            ConversationContextSnapshot snapshot = new()
            {
                Summary = compactedSummary,
                RecentMessages = PruneIfEnabled(recent.ToArray(), buildArgs),
                WasCompacted = true,
                SummaryTokensDropped = compactedDropped
            };

            if (foldStart < splitExclusive)
            {
                // WHY: The limiter runs BEFORE stamping so the fold marker (final line of the persisted
                // text) can never be trimmed away; the snapshot keeps the clean summary without the marker.
                persistedSummary = ConversationFoldMarker.Stamp(compactedSummary, history, splitExclusive);
            }

            return snapshot;
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
            if (history == null || history.Length == 0) return new ConversationContextSnapshot();
            string summaryRoleId = roleId;
            IAsyncConversationSummaryStore asyncStore = RequireAsyncStore(_summaryStore, ref summaryRoleId);
            string storedSummary = await asyncStore.LoadSummaryAsync(summaryRoleId, cancellationToken) ?? "";
            cancellationToken.ThrowIfCancellationRequested();
            ConversationContextSnapshot snapshot = ProjectSnapshot(roleId, history, roleConfig, buildArgs,
                storedSummary, out string persistedSummary);
            if (persistedSummary != null)
            {
                snapshot.CommitSummaryAsync = token => asyncStore.SaveSummaryAsync(summaryRoleId, persistedSummary, token);
                if (buildArgs?.DeferSummaryPersistence != true) await snapshot.CommitAsync(cancellationToken);
            }
            return snapshot;
        }

        internal static IAsyncConversationSummaryStore RequireAsyncStore(IConversationSummaryStore store, ref string roleId)
        {
            if (store is ScopedConversationSummaryStoreDecorator scoped) return scoped.BindAsync(roleId, out roleId);
            return store as IAsyncConversationSummaryStore ?? throw new NotSupportedException(
                "Async context preparation requires IAsyncConversationSummaryStore; explicitly wrap a sync-only backend in BlockingSyncSummaryStoreAsyncAdapter if blocking is acceptable.");
        }

        internal static int ResolveMessageLimit(AgentMemoryPolicy.RoleMemoryConfig roleConfig)
        {
            return roleConfig.MaxChatHistoryMessages > 0 ? roleConfig.MaxChatHistoryMessages : 30;
        }

        internal static (int splitExclusive, List<ChatMessage> recentTail) PartitionHistory(
            ChatMessage[] history, ITokenEstimator estimator, int tokenBudget,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig)
        {
            (int split, List<ChatMessage> recent) =
                ConversationHistoryPartition.PartitionByBudget(history, estimator, tokenBudget);
            int countSplit = Math.Max(0, history.Length - ResolveMessageLimit(roleConfig));
            if (countSplit > split)
            {
                recent.RemoveRange(0, countSplit - split);
                split = countSplit;
            }

            return (split, recent);
        }

        /// <summary>
        /// Cap on the summary this manager emits and persists: the caller's explicit
        /// <see cref="ConversationContextBuildArgs.MaxRolledSummaryTokens"/>, or 0 when there is none.
        /// </summary>
        internal static int ResolveSummaryTokenCap(ConversationContextBuildArgs buildArgs)
        {
            // WHY: Storing and sending are different jobs and must not share one bound. This manager
            // produces a single Summary value that is both persisted through CommitSummary and handed to
            // the orchestrator, so any bound applied here reaches the store. The store must stay whole:
            // every terminal path of a turn appends the user message, and on a bounded history store that
            // append evicts the oldest message, which is safe only because that message is already retold
            // in the stored summary. When this cap also folded in the request budget
            // (SummaryTokenBudget / HistoryTokenBudget), the truncated text was persisted and the oldest
            // retelling was gone for good. What has to fit the backend is the request, and the orchestrator
            // bounds its own copy of the summary to the reserve (AiOrchestrator.EnforceSummaryBudget)
            // without touching the snapshot. Only the user's explicit cap belongs here; do not merge the
            // request budget back in.
            return Math.Max(0, buildArgs?.MaxRolledSummaryTokens ?? 0);
        }

        private string LimitSummaryToBudget(
            string summary,
            ConversationContextBuildArgs buildArgs,
            out int droppedTokens)
        {
            droppedTokens = 0;
            string text = summary ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            int cap = ResolveSummaryTokenCap(buildArgs);
            string trimmed = text.Trim();
            if (cap <= 0)
            {
                return trimmed;
            }

            int before = _estimator.EstimateText(trimmed);
            if (before <= cap)
            {
                return trimmed;
            }

            // WHY: The limiter fits the kept suffix alone and then prefixes an ellipsis, which can cost one
            // more token; fitting the suffix one token short keeps the promise to the budget exact.
            string limited = ConversationRolledSummaryLimiter.Apply(trimmed, _estimator, Math.Max(1, cap - 1));
            droppedTokens = Math.Max(1, before - _estimator.EstimateText(limited));
            return limited;
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
