using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Deterministic pre-compaction context editing for prompt history copies.
    /// </summary>
    public static class ConversationHistoryPruner
    {
        private const string ThinkOpenTag = "<think>";
        private const string ThinkCloseTag = "</think>";

        /// <summary>
        /// Drops exact consecutive duplicate messages, strips stale <c>&lt;think&gt;</c> reasoning from
        /// every assistant turn except the newest one, removes fully superseded older tool-result messages,
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
            int staleThinkingChanged = StripStaleThinking(deduped, dropped);
            int supersededToolCount = MarkSupersededToolResults(deduped, dropped);
            int remainingToolCount = CountRemainingToolMessages(deduped, dropped);
            int staleToolCount = Math.Max(0, remainingToolCount - maxTools);

            int droppedCount = 0;
            for (int i = 0; i < dropped.Length; i++)
            {
                if (dropped[i])
                {
                    droppedCount++;
                }
            }

            if (duplicateCount == 0 && staleThinkingChanged == 0 && droppedCount == 0 && staleToolCount == 0)
            {
                return history;
            }

            ChatMessage[] pruned = new ChatMessage[deduped.Count - droppedCount - staleToolCount];
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

        /// <summary>
        /// Removes <c>&lt;think&gt;...&lt;/think&gt;</c> reasoning blocks from every assistant message except
        /// the newest one, because past chain-of-thought is scratch space the model does not need to re-read.
        /// Assistant messages that contain nothing but reasoning are marked dropped. The newest assistant turn
        /// keeps its reasoning intact. Returns the number of messages whose content changed (including emptied
        /// ones marked dropped). Operates on the in-memory list copy only.
        /// </summary>
        private static int StripStaleThinking(List<ChatMessage> messages, bool[] dropped)
        {
            int newestAssistant = -1;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (IsAssistantMessage(messages[i]))
                {
                    newestAssistant = i;
                    break;
                }
            }

            int changed = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                if (i == newestAssistant || dropped[i] || !IsAssistantMessage(messages[i]))
                {
                    continue;
                }

                string content = messages[i].Content;
                if (!ContainsThinkMarker(content))
                {
                    continue;
                }

                string stripped = StripThinkBlocks(content);
                if (string.Equals(stripped, content, StringComparison.Ordinal))
                {
                    continue;
                }

                changed++;
                if (string.IsNullOrWhiteSpace(stripped))
                {
                    dropped[i] = true;
                }
                else
                {
                    ChatMessage updated = messages[i];
                    updated.Content = stripped;
                    messages[i] = updated;
                }
            }

            return changed;
        }

        private static bool ContainsThinkMarker(string content)
        {
            return !string.IsNullOrEmpty(content) &&
                   (content.IndexOf(ThinkOpenTag, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    content.IndexOf(ThinkCloseTag, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Removes well-formed <c>&lt;think&gt;...&lt;/think&gt;</c> spans and orphan reasoning that ends in a
        /// stray <c>&lt;/think&gt;</c> (some reasoning models stream hidden text without an opening tag). An
        /// unterminated opening tag drops the remainder. The result is trimmed of surrounding whitespace.
        /// </summary>
        private static string StripThinkBlocks(string content)
        {
            StringBuilder sb = new(content.Length);
            int i = 0;
            int n = content.Length;

            while (i < n)
            {
                int open = content.IndexOf(ThinkOpenTag, i, StringComparison.OrdinalIgnoreCase);
                int close = content.IndexOf(ThinkCloseTag, i, StringComparison.OrdinalIgnoreCase);

                // Orphan close before any open: treat the leading text as hidden reasoning and drop it.
                if (close >= 0 && (open < 0 || close < open))
                {
                    i = close + ThinkCloseTag.Length;
                    continue;
                }

                if (open < 0)
                {
                    sb.Append(content, i, n - i);
                    break;
                }

                if (open > i)
                {
                    sb.Append(content, i, open - i);
                }

                int afterOpen = open + ThinkOpenTag.Length;
                int matchingClose = content.IndexOf(ThinkCloseTag, afterOpen, StringComparison.OrdinalIgnoreCase);
                if (matchingClose < 0)
                {
                    // Unterminated reasoning block: drop everything to the end.
                    break;
                }

                i = matchingClose + ThinkCloseTag.Length;
            }

            return sb.ToString().Trim();
        }

        private static bool IsAssistantMessage(ChatMessage message)
        {
            return string.Equals(message.Role, "assistant", StringComparison.Ordinal);
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