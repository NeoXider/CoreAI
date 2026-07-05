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
        public static string Format(string existingSummary, ChatMessage[] history, int splitExclusive)
        {
            if (history == null || splitExclusive <= 0)
            {
                return "";
            }

            StringBuilder sb = new();
            if (!string.IsNullOrWhiteSpace(existingSummary))
            {
                sb.AppendLine(existingSummary.Trim());
                sb.AppendLine();
            }

            sb.AppendLine("Previous conversation summary:");
            for (int i = 0; i < splitExclusive; i++)
            {
                string role = string.IsNullOrWhiteSpace(history[i].Role) ? "unknown" : history[i].Role.Trim();
                string content = history[i].Content ?? "";
                if (content.Length > 280)
                {
                    content = content.Substring(0, 280).TrimEnd() + "...";
                }

                sb.Append("- ").Append(role).Append(": ").AppendLine(content);
            }

            return sb.ToString().Trim();
        }
    }
}