using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Deterministic pre-compaction context editing for prompt history copies.
    /// </summary>
    public static class ConversationHistoryPruner
    {
        /// <summary>
        /// Drops exact consecutive duplicate messages, then keeps only the newest tool-result messages.
        /// The input array and durable stores are never mutated.
        /// </summary>
        public static ChatMessage[] Prune(ChatMessage[] history, int maxRetainedToolResultMessages)
        {
            if (history == null || history.Length == 0)
            {
                return history;
            }

            int maxTools = Math.Max(0, maxRetainedToolResultMessages);
            int keptAfterDedupe = 0;
            int duplicateCount = 0;
            int toolCount = 0;
            ChatMessage previousKept = default;
            bool hasPreviousKept = false;

            for (int i = 0; i < history.Length; i++)
            {
                ChatMessage current = history[i];
                if (hasPreviousKept && IsExactConsecutiveDuplicate(previousKept, current))
                {
                    duplicateCount++;
                    continue;
                }

                keptAfterDedupe++;
                if (IsToolMessage(current))
                {
                    toolCount++;
                }

                previousKept = current;
                hasPreviousKept = true;
            }

            int staleToolCount = Math.Max(0, toolCount - maxTools);
            if (duplicateCount == 0 && staleToolCount == 0)
            {
                return history;
            }

            ChatMessage[] pruned = new ChatMessage[keptAfterDedupe - staleToolCount];
            int write = 0;
            int skippedTools = 0;
            previousKept = default;
            hasPreviousKept = false;

            for (int i = 0; i < history.Length; i++)
            {
                ChatMessage current = history[i];
                if (hasPreviousKept && IsExactConsecutiveDuplicate(previousKept, current))
                {
                    continue;
                }

                if (IsToolMessage(current) && skippedTools < staleToolCount)
                {
                    skippedTools++;
                    previousKept = current;
                    hasPreviousKept = true;
                    continue;
                }

                pruned[write++] = current;
                previousKept = current;
                hasPreviousKept = true;
            }

            return pruned;
        }

        private static bool IsToolMessage(ChatMessage message)
        {
            return string.Equals(message.Role, "tool", StringComparison.Ordinal);
        }

        private static bool IsExactConsecutiveDuplicate(ChatMessage left, ChatMessage right)
        {
            return string.Equals(left.Role, right.Role, StringComparison.Ordinal) &&
                   TrimmedContentEquals(left.Content, right.Content);
        }

        private static bool TrimmedContentEquals(string left, string right)
        {
            int leftStart;
            int leftLength;
            int rightStart;
            int rightLength;
            GetTrimmedRange(left, out leftStart, out leftLength);
            GetTrimmedRange(right, out rightStart, out rightLength);
            if (leftLength != rightLength)
            {
                return false;
            }

            for (int i = 0; i < leftLength; i++)
            {
                if (left[leftStart + i] != right[rightStart + i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void GetTrimmedRange(string value, out int start, out int length)
        {
            if (string.IsNullOrEmpty(value))
            {
                start = 0;
                length = 0;
                return;
            }

            start = 0;
            int end = value.Length - 1;
            while (start <= end && char.IsWhiteSpace(value[start]))
            {
                start++;
            }

            while (end >= start && char.IsWhiteSpace(value[end]))
            {
                end--;
            }

            length = end - start + 1;
        }
    }
}
