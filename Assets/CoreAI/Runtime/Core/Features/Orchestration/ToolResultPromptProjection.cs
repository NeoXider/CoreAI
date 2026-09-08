using System;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Renders durable <c>tool</c> history messages into the prompt in a MACHINE register.
    /// <para>
    /// The durable block written by <c>AiOrchestrator.BuildToolResultsMemoryBlock</c> is markdown:
    /// a <c>## Tool Results</c> heading over <c>- name: ok {payload}</c> bullets. That shape is fine on
    /// disk, but the prompt used to receive it verbatim as one more chat message — the very register the
    /// model itself speaks in. Models imitate what they see there, and a learner read this in the lesson
    /// chat, inside the teacher's own reply:
    /// <code>
    /// ## Tool Results
    /// - spawn_quiz: ok {"success":true,"tool":"spawn_quiz",...}
    /// </code>
    /// Nothing in the UI produced it: the model copied our own bookkeeping format back at a child.
    /// </para>
    /// <para>
    /// So the projection strips the heading and re-emits every entry as a self-describing
    /// <c>tool_result name=… status=… result=…</c> line: a log record, not a chat turn, not markdown, and
    /// nothing that reads like something the assistant would say. Lines the engine did not write (the
    /// indented <c>Full</c>-policy payload) pass through untouched — that text belongs to the tool.
    /// </para>
    /// <para>
    /// The projection deliberately runs at prompt-build time rather than at write time: it then also
    /// covers history persisted by older versions, and the on-disk encoding (which
    /// <see cref="ConversationHistoryPruner"/> parses) keeps working unchanged.
    /// </para>
    /// <para>
    /// The transport role stays <c>user</c>. A real <c>tool</c> role is not an option here: on
    /// OpenAI-compatible endpoints a tool message must pair with a <c>tool_call_id</c> from the assistant
    /// message right before it, and durable history carries no such pairing — the provider answers 400.
    /// The register, not the transport role, is what the model was imitating.
    /// </para>
    /// </summary>
    internal static class ToolResultPromptProjection
    {
        /// <summary>Durable role marking a persisted tool-result block.</summary>
        internal const string ToolHistoryRole = "tool";

        /// <summary>Machine-register prefix that opens every projected entry line.</summary>
        internal const string EntryPrefix = "tool_result ";

        private const string LegacyHeader = "## Tool Results";
        private const string OkStatus = "ok";
        private const string FailedStatus = "FAILED";

        /// <summary>
        /// Returns the prompt text for one durable history message. Only <c>tool</c> messages are
        /// re-rendered; every other role is passed through byte for byte.
        /// </summary>
        internal static string ForPrompt(string role, string content)
        {
            return string.Equals(role, ToolHistoryRole, StringComparison.Ordinal)
                ? ProjectBlock(content)
                : content;
        }

        /// <summary>
        /// Rewrites a durable tool-result block into the machine register. Content that does not look
        /// like a block this engine wrote is returned unchanged: mangling a host's own message would be
        /// a worse failure than leaving it as it is.
        /// </summary>
        internal static string ProjectBlock(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            StringBuilder projected = new(normalized.Length + 32);
            bool sawHeader = false;
            bool sawEntry = false;
            bool first = true;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!sawHeader && string.Equals(line.Trim(), LegacyHeader, StringComparison.Ordinal))
                {
                    sawHeader = true;
                    continue;
                }

                if (!first)
                {
                    projected.Append('\n');
                }

                first = false;
                if (TryProjectEntry(line, out string entry))
                {
                    sawEntry = true;
                    projected.Append(entry);
                    continue;
                }

                projected.Append(line);
            }

            return sawHeader || sawEntry ? projected.ToString() : content;
        }

        /// <summary>
        /// Recognizes one durable entry bullet and rewrites it as a key=value record.
        /// <para>
        /// The recognition rules are the ones <see cref="ConversationHistoryPruner"/> already relies on,
        /// and for the same reason: under the <c>Full</c> policy an entry is followed by INDENTED tool
        /// output that may itself contain lines like <c>  - foo: bar</c>. An entry is therefore a bullet
        /// at column 0 whose value starts with a known status token; anything else is payload.
        /// </para>
        /// </summary>
        private static bool TryProjectEntry(string line, out string entry)
        {
            entry = null;
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

            string status = ReadStatus(line, colon + 1, out int statusEnd);
            if (status == null)
            {
                return false;
            }

            string name = line.Substring(start, colon - start).Trim();
            if (name.Length == 0)
            {
                return false;
            }

            string detail = line.Substring(statusEnd).Trim();
            StringBuilder sb = new(line.Length + 32);
            sb.Append(EntryPrefix).Append("name=").Append(name);
            sb.Append(" status=").Append(string.Equals(status, OkStatus, StringComparison.Ordinal)
                ? OkStatus
                : "failed");
            if (detail.Length > 0)
            {
                sb.Append(" result=").Append(detail);
            }

            entry = sb.ToString();
            return true;
        }

        /// <summary>
        /// Returns the status token the value starts with, or <c>null</c> when the line only resembles an
        /// entry. A token must end the line or be followed by a space, so <c>okay</c> is not <c>ok</c>.
        /// </summary>
        private static string ReadStatus(string line, int index, out int statusEnd)
        {
            while (index < line.Length && line[index] == ' ')
            {
                index++;
            }

            if (MatchesStatus(line, index, OkStatus))
            {
                statusEnd = index + OkStatus.Length;
                return OkStatus;
            }

            if (MatchesStatus(line, index, FailedStatus))
            {
                statusEnd = index + FailedStatus.Length;
                return FailedStatus;
            }

            statusEnd = index;
            return null;
        }

        private static bool MatchesStatus(string line, int index, string token)
        {
            if (index + token.Length > line.Length ||
                string.CompareOrdinal(line, index, token, 0, token.Length) != 0)
            {
                return false;
            }

            int after = index + token.Length;
            return after == line.Length || line[after] == ' ';
        }
    }
}
