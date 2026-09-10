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

            // WHY: Compaction folds the old prefix into the durable rolling summary, so it must see
            // the FULL history. Pruning first would drop superseded tool results before they are
            // summarized and they would vanish from every future prompt without a trace. Pruning
            // still applies, but only to the emitted recent tail (prompt-level noise control).
            string storedSummary = _summaryStore.LoadSummary(roleId) ?? "";
            // WHY: The persisted summary carries a machine-only fold marker as its final line; every
            // snapshot-facing path must see only the clean prose.
            string cleanStoredSummary = ConversationFoldMarker.Strip(storedSummary);
            // WHY: The recent tail is bounded by its own budget whether or not a summary exists; the
            // summary reserve is never lent to the tail, so an explicit recent-tail override keeps its
            // meaning and the emitted request can never exceed tail budget plus summary reserve.
            int historyBudget = ConversationContextBudgetTokens.ResolveHistoryChatBudget(roleConfig, buildArgs);
            if (history.Length <= ResolveMessageLimit(roleConfig) &&
                !ConversationContextBudgetTokens.ShouldPartitionForCompaction(
                    history,
                    _estimator,
                    historyBudget,
                    buildArgs))
            {
                string storedOut = LimitSummaryToBudget(
                    cleanStoredSummary, buildArgs, historyBudget, out int storedDropped);
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
                string summaryOut = LimitSummaryToBudget(
                    cleanStoredSummary, buildArgs, historyBudget, out int summaryDropped);
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
                historyBudget,
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
        /// Effective summary cap: the explicit <see cref="ConversationContextBuildArgs.MaxRolledSummaryTokens"/>
        /// when set, never more than the request budget.
        /// </summary>
        internal static int ResolveSummaryTokenCap(ConversationContextBuildArgs buildArgs, int historyBudget)
        {
            int explicitCap = buildArgs?.MaxRolledSummaryTokens ?? 0;
            int reserved = buildArgs?.SummaryTokenBudget ?? 0;
            // WHY: An explicit cap of zero keeps its documented meaning (ICoreAISettings: "no cap") but is
            // never "unbounded": a persisted summary that outgrew the window made every later turn fail
            // against a healthy backend. The request budget applies regardless of the explicit cap, and
            // without a reserved summary budget the recent-tail budget is the ceiling, so a raw caller can
            // never emit more summary than it allows for live history.
            int budgetCap = reserved > 0 ? reserved : Math.Max(1, historyBudget);
            return explicitCap > 0 ? Math.Min(explicitCap, budgetCap) : budgetCap;
        }

        private string LimitSummaryToBudget(
            string summary,
            ConversationContextBuildArgs buildArgs,
            int historyBudget,
            out int droppedTokens)
        {
            droppedTokens = 0;
            string text = summary ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            int cap = ResolveSummaryTokenCap(buildArgs, historyBudget);
            string trimmed = text.Trim();
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
