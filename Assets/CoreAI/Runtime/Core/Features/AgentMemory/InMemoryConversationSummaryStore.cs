using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Process-wide summary store for <see cref="DeterministicConversationContextManager"/>.
    /// Keeps accumulated <c>## Conversation Summary</c> text per role in memory until cleared or the process exits.
    /// </summary>
    public sealed class InMemoryConversationSummaryStore : IConversationSummaryStore
    {
        private readonly object _lock = new();

        private readonly Dictionary<string, string> _byRole =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public string LoadSummary(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return "";
            }

            string key = roleId.Trim();
            lock (_lock)
            {
                return _byRole.TryGetValue(key, out string s) ? s : "";
            }
        }

        /// <inheritdoc />
        public void SaveSummary(string roleId, string summary)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            string key = roleId.Trim();
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(summary))
                {
                    _byRole.Remove(key);
                }
                else
                {
                    _byRole[key] = summary;
                }
            }
        }

        /// <inheritdoc />
        public void ClearSummary(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            string key = roleId.Trim();
            lock (_lock)
            {
                _byRole.Remove(key);
            }
        }
    }
}