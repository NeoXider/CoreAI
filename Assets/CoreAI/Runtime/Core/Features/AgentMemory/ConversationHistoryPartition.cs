using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Shared history token budget resolution (orchestrator-aligned with <see cref="ConversationContextBuildArgs.HistoryTokenBudget"/>).
    /// </summary>
    internal static class ConversationContextBudgetTokens
    {
        internal static int ResolveHistoryChatBudget(
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            ConversationContextBuildArgs buildArgs)
        {
            if (buildArgs != null && buildArgs.HistoryTokenBudget > 0)
            {
                return Math.Max(1, buildArgs.HistoryTokenBudget);
            }

            int maxTokens = roleConfig.ContextTokens > 0
                ? roleConfig.ContextTokens
                : CoreAISettings.DefaultContextWindowTokens;
            return Math.Max(1, maxTokens / 2);
        }

        internal static int EstimateHistoryTokens(ChatMessage[] history, ITokenEstimator estimator)
        {
            if (history == null || history.Length == 0)
            {
                return 0;
            }

            long total = 0;
            for (int i = 0; i < history.Length; i++)
            {
                total += Math.Max(0, estimator.EstimateText(history[i].Content ?? ""));
                if (total >= int.MaxValue)
                {
                    return int.MaxValue;
                }
            }

            return (int)total;
        }

        internal static float ResolveCompactionTriggerRatio(ConversationContextBuildArgs buildArgs)
        {
            float ratio = buildArgs?.CompactionTriggerRatio ?? 0f;
            if (ratio <= 0f || ratio > 1f || float.IsNaN(ratio) || float.IsInfinity(ratio))
            {
                return CoreAISettings.DefaultConversationCompactionTriggerRatio;
            }

            return ratio;
        }

        internal static bool ShouldPartitionForCompaction(
            ChatMessage[] history,
            ITokenEstimator estimator,
            int historyBudget,
            ConversationContextBuildArgs buildArgs)
        {
            int totalHistoryTokens = EstimateHistoryTokens(history, estimator);
            double triggerTokens = historyBudget * (double)ResolveCompactionTriggerRatio(buildArgs);
            return totalHistoryTokens >= triggerTokens;
        }
    }

    /// <summary>
    /// Keeps the newest dialogue tail within a heuristic token budget; older prefix is summarized separately.
    /// </summary>
    internal static class ConversationHistoryPartition
    {
        /// <summary>
        /// Returns the exclusive index at which verbatim tail starts (<c>history[splitExclusive..]</c> kept).
        /// </summary>
        public static (int splitExclusive, List<ChatMessage> recentTail) PartitionByBudget(
            ChatMessage[] history,
            ITokenEstimator estimator,
            int budgetTokens)
        {
            List<ChatMessage> recent = new();
            int splitExclusive = history.Length;

            int budgetRemaining = budgetTokens;
            for (int i = history.Length - 1; i >= 0; i--)
            {
                int estimatedTokens = estimator.EstimateText(history[i].Content);
                if (budgetRemaining - estimatedTokens < 0 && recent.Count > 0)
                {
                    splitExclusive = i + 1;
                    break;
                }

                budgetRemaining -= estimatedTokens;
                recent.Insert(0, history[i]);
                splitExclusive = i;
            }

            return (splitExclusive, recent);
        }
    }

    internal static class ConversationBulletSummary
    {
        public static string Format(
            string existingSummary,
            ChatMessage[] history,
            int splitExclusive,
            int startInclusive = 0)
        {
            if (history == null || splitExclusive <= startInclusive)
            {
                return existingSummary?.Trim() ?? "";
            }

            StringBuilder sb = new();
            if (!string.IsNullOrWhiteSpace(existingSummary))
            {
                sb.AppendLine(existingSummary.Trim());
            }
            else
            {
                sb.AppendLine("Previous conversation summary:");
            }

            for (int i = Math.Max(0, startInclusive); i < splitExclusive; i++)
            {
                // WHY: A whitespace-only message would emit a bare "- role: " bullet; besides being noise,
                // such a bullet as the persisted watermark line can never be re-matched by FindFoldStart
                // (blank contents are skipped there), which would refold the whole prefix every turn.
                if (string.IsNullOrWhiteSpace(history[i].Content))
                {
                    continue;
                }

                sb.AppendLine(FormatMessage(history[i]));
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Re-detects the already-folded prefix: the watermark bullet of the last folded non-empty message
        /// is always stamped as the final line of the persisted summary, so only a whole-final-line match
        /// counts. Substring matches (blank "- user: " bullets, prefix subsumption like "hel" inside
        /// "hello", duplicate messages matching old mid-summary bullets) are rejected by design.
        /// </summary>
        public static int FindFoldStart(string existingSummary, ChatMessage[] history, int splitExclusive)
        {
            if (string.IsNullOrWhiteSpace(existingSummary) || history == null || splitExclusive <= 0)
            {
                return 0;
            }

            int foldStart = Math.Min(splitExclusive, history.Length);
            for (int i = foldStart - 1; i >= 0; i--)
            {
                // WHY: Blank contents format to "- role: ", a substring of every same-role bullet; they can
                // never be a reliable watermark. Skipping them also keeps whitespace-only tails between the
                // watermark and the live tail from triggering pointless refold passes (folding them is a no-op).
                if (string.IsNullOrWhiteSpace(history[i].Content))
                {
                    continue;
                }

                if (IsFinalLine(existingSummary, FormatMessage(history[i])))
                {
                    return foldStart;
                }

                foldStart = i;
            }

            return 0;
        }

        /// <summary>
        /// Returns the index of the newest message before <paramref name="splitExclusive"/> with
        /// non-whitespace content (the message whose bullet serves as the persisted watermark), or -1.
        /// </summary>
        public static int FindWatermarkIndex(ChatMessage[] history, int splitExclusive)
        {
            if (history == null)
            {
                return -1;
            }

            for (int i = Math.Min(splitExclusive, history.Length) - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(history[i].Content))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsFinalLine(string summary, string bullet)
        {
            if (bullet.Length == 0 || !summary.EndsWith(bullet, StringComparison.Ordinal))
            {
                return false;
            }

            int lineStart = summary.Length - bullet.Length;
            return lineStart == 0 || summary[lineStart - 1] == '\n';
        }

        private static string FormatMessage(ChatMessage message)
        {
            string role = string.IsNullOrWhiteSpace(message.Role) ? "unknown" : message.Role.Trim();
            string content = message.Content ?? "";
            if (content.Length > 280)
            {
                content = content.Substring(0, 280).TrimEnd() + "...";
            }

            return "- " + role + ": " + content;
        }
    }
}
