using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Default context manager that keeps recent turns and creates a deterministic summary for older history.
    /// </summary>
    public sealed class DeterministicConversationContextManager : IConversationContextManager
    {
        private readonly IConversationSummaryStore _summaryStore;

        /// <summary>
        /// Creates a new deterministic context manager.
        /// </summary>
        public DeterministicConversationContextManager(IConversationSummaryStore summaryStore)
        {
            _summaryStore = summaryStore ?? throw new ArgumentNullException(nameof(summaryStore));
        }

        /// <inheritdoc />
        public ConversationContextSnapshot BuildSnapshot(
            string roleId,
            ChatMessage[] history,
            AgentMemoryPolicy.RoleMemoryConfig roleConfig)
        {
            if (history == null || history.Length == 0)
            {
                return new ConversationContextSnapshot();
            }

            int maxTokens = roleConfig.ContextTokens > 0 ? roleConfig.ContextTokens : 8192;
            int budgetTokens = Math.Max(1, maxTokens / 2);
            List<ChatMessage> recent = new();
            int splitIndex = history.Length;

            for (int i = history.Length - 1; i >= 0; i--)
            {
                int estimatedTokens = EstimateTokens(history[i].Content);
                if (budgetTokens - estimatedTokens < 0 && recent.Count > 0)
                {
                    splitIndex = i + 1;
                    break;
                }

                budgetTokens -= estimatedTokens;
                recent.Insert(0, history[i]);
                splitIndex = i;
            }

            string storedSummary = _summaryStore.LoadSummary(roleId) ?? "";
            if (splitIndex <= 0)
            {
                return new ConversationContextSnapshot
                {
                    Summary = storedSummary,
                    RecentMessages = recent.ToArray(),
                    WasCompacted = !string.IsNullOrWhiteSpace(storedSummary)
                };
            }

            string compactedSummary = BuildSummary(storedSummary, history, splitIndex);
            _summaryStore.SaveSummary(roleId, compactedSummary);

            return new ConversationContextSnapshot
            {
                Summary = compactedSummary,
                RecentMessages = recent.ToArray(),
                WasCompacted = true
            };
        }

        private static int EstimateTokens(string content)
        {
            return string.IsNullOrEmpty(content) ? 0 : Math.Max(1, content.Length / 3);
        }

        private static string BuildSummary(string existingSummary, ChatMessage[] history, int count)
        {
            StringBuilder sb = new();
            if (!string.IsNullOrWhiteSpace(existingSummary))
            {
                sb.AppendLine(existingSummary.Trim());
                sb.AppendLine();
            }

            sb.AppendLine("Previous conversation summary:");
            for (int i = 0; i < count; i++)
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
