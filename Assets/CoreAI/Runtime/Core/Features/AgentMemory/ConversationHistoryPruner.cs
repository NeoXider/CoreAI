using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Deterministic pre-compaction context editing for prompt history copies.
    /// </summary>
    public static class ConversationHistoryPruner
    {
        /// <summary>
        /// Drops exact consecutive duplicate messages, removes fully superseded older tool-result messages,
        /// then keeps only the newest tool-result messages.
        /// The input array and durable stores are never mutated.
        /// </summary>
        public static ChatMessage[] Prune(ChatMessage[] history, int maxRetainedToolResultMessages)
        {
            if (history == null || history.Length == 0)
            {
                return history;
            }

            int maxTools = Math.Max(0, maxRetainedToolResultMessages);
            int duplicateCount = 0;
            List<ChatMessage> deduped = new(history.Length);
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

                deduped.Add(current);
                previousKept = current;
                hasPreviousKept = true;
            }

            bool[] dropped = new bool[deduped.Count];
            int supersededToolCount = MarkSupersededToolResults(deduped, dropped);
            int remainingToolCount = CountRemainingToolMessages(deduped, dropped);
            int staleToolCount = Math.Max(0, remainingToolCount - maxTools);
            if (duplicateCount == 0 && supersededToolCount == 0 && staleToolCount == 0)
            {
                return history;
            }

            ChatMessage[] pruned = new ChatMessage[deduped.Count - supersededToolCount - staleToolCount];
            int write = 0;
            int skippedTools = 0;

            for (int i = 0; i < deduped.Count; i++)
            {
                if (dropped[i])
                {
                    continue;
                }

                ChatMessage current = deduped[i];
                if (IsToolMessage(current) && skippedTools < staleToolCount)
                {
                    skippedTools++;
                    continue;
                }

                pruned[write++] = current;
            }

            return pruned;
        }

        private static int MarkSupersededToolResults(List<ChatMessage> messages, bool[] dropped)
        {
            HashSet<string> newerToolNames = new(StringComparer.Ordinal);
            int supersededCount = 0;

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (!IsToolMessage(messages[i]))
                {
                    continue;
                }

                List<string> toolNames = ExtractToolNames(messages[i].Content);
                if (toolNames.Count == 0)
                {
                    continue;
                }

                bool allSuperseded = true;
                for (int n = 0; n < toolNames.Count; n++)
                {
                    if (!newerToolNames.Contains(toolNames[n]))
                    {
                        allSuperseded = false;
                        break;
                    }
                }

                if (allSuperseded)
                {
                    dropped[i] = true;
                    supersededCount++;
                    continue;
                }

                for (int n = 0; n < toolNames.Count; n++)
                {
                    newerToolNames.Add(toolNames[n]);
                }
            }

            return supersededCount;
        }

        private static int CountRemainingToolMessages(List<ChatMessage> messages, bool[] dropped)
        {
            int count = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                if (!dropped[i] && IsToolMessage(messages[i]))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsToolMessage(ChatMessage message)
        {
            return string.Equals(message.Role, "tool", StringComparison.Ordinal);
        }

        private static List<string> ExtractToolNames(string content)
        {
            List<string> toolNames = new();
            if (string.IsNullOrWhiteSpace(content))
            {
                return toolNames;
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int start = 0;
                while (start < line.Length && char.IsWhiteSpace(line[start]))
                {
                    start++;
                }

                if (start >= line.Length || line[start] != '-')
                {
                    continue;
                }

                start++;
                while (start < line.Length && char.IsWhiteSpace(line[start]))
                {
                    start++;
                }

                int colon = line.IndexOf(':', start);
                if (colon <= start)
                {
                    continue;
                }

                string name = line.Substring(start, colon - start).Trim();
                if (name.Length > 0 && seen.Add(name))
                {
                    toolNames.Add(name);
                }
            }

            return toolNames;
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
