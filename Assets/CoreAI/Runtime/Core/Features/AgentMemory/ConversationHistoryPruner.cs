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
        /// every assistant turn except the newest one, removes older tool-result messages whose every
        /// entry (same tool, same recorded result) is repeated verbatim by a newer block,
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

                // WHY: Orphan close before any open: treat the leading text as hidden reasoning and drop it.
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
                    // WHY: Unterminated reasoning block: drop everything to the end.
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

        /// <summary>
        /// Marks a tool block as "superseded" when EVERY one of its entries is repeated verbatim in a
        /// newer block: same tool and same recorded output.
        /// <para>
        /// Superseding used to be decided by a match on the tool NAME alone. The name in a trace is the
        /// name of the outer call, and in the RedoSchool teacher nearly everything goes through a single
        /// skill router, <c>call_skill_tool</c>: the next presentation slide "superseded" the code station
        /// output the child is asking about right now, and the result vanished from the prompt. The
        /// durable block does not store the call arguments (<c>LlmToolCallTrace</c> carries only the name
        /// and the result), so the only identity that can be PROVEN from the data is "same tool + same
        /// recorded result". Such an entry in the newer block carries exactly the same information and the
        /// older copy is redundant; everything else is a different call, and the pruner has no right to
        /// decide on the model's behalf which of them is stale.
        /// </para>
        /// </summary>
        private static int MarkSupersededToolResults(List<ChatMessage> messages, bool[] dropped)
        {
            HashSet<string> newerEntryKeys = new(StringComparer.Ordinal);
            int supersededCount = 0;

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (!IsToolMessage(messages[i]))
                {
                    continue;
                }

                List<string> entryKeys = ExtractToolEntryKeys(messages[i].Content);
                if (entryKeys.Count == 0)
                {
                    continue;
                }

                bool allSuperseded = true;
                for (int n = 0; n < entryKeys.Count; n++)
                {
                    if (!newerEntryKeys.Contains(entryKeys[n]))
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

                for (int n = 0; n < entryKeys.Count; n++)
                {
                    newerEntryKeys.Add(entryKeys[n]);
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

        /// <summary>
        /// Extracts, from the durable "## Tool Results" block (written by
        /// <c>AiOrchestrator.BuildToolResultsMemoryBlock</c>), the identity key of every entry: the entry
        /// line <c>- {name}: {ok|FAILED}[ detail]</c> together with all of its indented lines (under the
        /// <c>Full</c> policy that is the <c>  Detail:</c> block holding the tool output). The key is the
        /// name AND the result: a name without a result does not tell two calls of the same tool apart
        /// (see <see cref="MarkSupersededToolResults"/>).
        /// <para>
        /// Entry recognition is the same one <see cref="ToolResultPromptProjection"/> relies on: under
        /// <c>Full</c> the tool output itself may contain lines shaped like <c>  - foo: bar</c>
        /// (markdown/YAML/diff), and a naive "first '-' ... first ':'" parse would take them for entries.
        /// So an entry is only (1) a bullet in column zero (nested output is always indented) and (2) a
        /// value after the colon that starts with a known status (<c>ok</c> / <c>FAILED</c>); anything
        /// else is the tail of the current entry.
        /// </para>
        /// </summary>
        private static List<string> ExtractToolEntryKeys(string content)
        {
            List<string> entryKeys = new();
            if (string.IsNullOrWhiteSpace(content))
            {
                return entryKeys;
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            StringBuilder currentEntry = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                if (IsEntryLine(line))
                {
                    AddEntryKey(entryKeys, seen, currentEntry);
                    currentEntry = new StringBuilder(line);
                    continue;
                }

                // WHY: A line that is not entry-shaped and follows an entry is that entry's output (Detail
                // under Full), and it is part of the identity: the same tool with a different output is a
                // different call. Blank tail lines and the header before the first entry identify nothing
                // and stay out of the key.
                if (currentEntry != null && line.Length > 0)
                {
                    currentEntry.Append('\n').Append(line);
                }
            }

            AddEntryKey(entryKeys, seen, currentEntry);
            return entryKeys;
        }

        private static void AddEntryKey(List<string> entryKeys, HashSet<string> seen, StringBuilder entry)
        {
            if (entry == null)
            {
                return;
            }

            string key = entry.ToString();
            if (seen.Add(key))
            {
                entryKeys.Add(key);
            }
        }

        /// <summary>True for a column-zero bullet <c>- name: ok|FAILED ...</c> with a non-empty name.</summary>
        private static bool IsEntryLine(string line)
        {
            if (line.Length < 2 || line[0] != '-' || line[1] != ' ')
            {
                return false;
            }

            const int start = 2;
            int colon = line.IndexOf(':', start);
            if (colon <= start)
            {
                return false;
            }

            if (!ValueStartsWithStatus(line, colon + 1))
            {
                return false;
            }

            return line.Substring(start, colon - start).Trim().Length > 0;
        }

        /// <summary>True when the text after a tool entry's colon begins with "ok" or "FAILED".</summary>
        private static bool ValueStartsWithStatus(string line, int valueStart)
        {
            int p = valueStart;
            while (p < line.Length && line[p] == ' ')
            {
                p++;
            }

            return StartsWithToken(line, p, "ok") || StartsWithToken(line, p, "FAILED");
        }

        /// <summary>Ordinal match of <paramref name="token"/> at <paramref name="pos"/>, ended by end-of-line or a space.</summary>
        private static bool StartsWithToken(string line, int pos, string token)
        {
            if (pos + token.Length > line.Length)
            {
                return false;
            }

            for (int i = 0; i < token.Length; i++)
            {
                if (line[pos + i] != token[i])
                {
                    return false;
                }
            }

            int after = pos + token.Length;
            return after >= line.Length || line[after] == ' ';
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
