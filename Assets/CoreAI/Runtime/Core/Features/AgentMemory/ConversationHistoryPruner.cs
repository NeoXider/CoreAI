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
        /// Помечает tool-блок «перекрытым», когда КАЖДАЯ его запись дословно повторяется в более новом
        /// блоке: тот же инструмент и та же записанная выдача.
        /// <para>
        /// Раньше перекрытием считалось совпадение одного лишь ИМЕНИ инструмента. Имя в трассе — это имя
        /// внешнего вызова, а у учителя RedoSchool почти всё идёт через один скилл-роутер
        /// <c>call_skill_tool</c>: следующий слайд презентации «перекрывал» вывод код-станции, про который
        /// ребёнок в эту минуту спрашивает, и результат исчезал из промпта. Аргументов вызова durable-блок
        /// не хранит (<c>LlmToolCallTrace</c> несёт только имя и результат), поэтому единственная
        /// идентичность, которую можно ДОКАЗАТЬ по данным, — «тот же инструмент + та же запись результата».
        /// Такая запись в новом блоке несёт ровно ту же информацию, и старшая копия избыточна; всё
        /// остальное — разные вызовы, и решать за модель, что из них устарело, пруннер не вправе.
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
        /// Извлекает из durable-блока «## Tool Results» (его пишет <c>AiOrchestrator.BuildToolResultsMemoryBlock</c>)
        /// ключ идентичности каждой записи: строку записи <c>- {name}: {ok|FAILED}[ detail]</c> вместе со всеми
        /// её отступными строками (под политикой <c>Full</c> это блок <c>  Detail:</c> с выдачей инструмента).
        /// Ключ — это имя И результат: одно имя без результата не отличает два вызова одного инструмента
        /// (см. <see cref="MarkSupersededToolResults"/>).
        /// <para>
        /// Распознавание записи то же, на которое опирается <see cref="ToolResultPromptProjection"/>: выдача
        /// инструмента под <c>Full</c> сама может содержать строки вида <c>  - foo: bar</c> (markdown/YAML/diff),
        /// и наивный разбор «первый '-' … первый ':'» принял бы их за записи. Поэтому запись — это только
        /// (1) bullet в нулевой колонке (вложенная выдача всегда с отступом) и (2) значение после двоеточия,
        /// начинающееся с известного статуса (<c>ok</c> / <c>FAILED</c>); всё прочее — хвост текущей записи.
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

                // WHY: Строка без формы записи после записи — это её выдача (Detail под Full), и она часть
                // идентичности: тот же инструмент с другой выдачей — другой вызов. Пустые строки в хвосте
                // и заголовок до первой записи ничего не идентифицируют и в ключ не идут.
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

        /// <summary>True для bullet нулевой колонки <c>- name: ok|FAILED …</c> с непустым именем.</summary>
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
