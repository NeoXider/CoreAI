using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Stateful stream filter that removes a VERBATIM repetition of a tool result the engine itself
    /// handed the model.
    /// <para>
    /// It is the twin of <c>LlmToolCallTextExtractor.StripForDisplay</c>, which does the same for a
    /// leaked tool CALL. The missing half cost a lesson: after <c>spawn_quiz</c> the teacher's reply
    /// contained the raw result envelope
    /// (<c>{"success":true,"tool":"spawn_quiz","status":"card_shown_waiting_for_student",…}</c>) and the
    /// learner read the engine's bookkeeping instead of a question.
    /// </para>
    /// <para>
    /// Matching is by IDENTITY, never by keyword: the only strings removed are the exact ones the engine
    /// produced and fed back (<c>LlmToolCallTrace.Detail</c>). A substring filter would eventually eat a
    /// legitimate sentence, and the moment it did nobody would be able to reproduce why.
    /// </para>
    /// <para>
    /// It must run ON THE STREAM, not after it: on the streaming path the text is already on the
    /// learner's screen by the time the turn is sanitized. Like <see cref="ThinkBlockStreamFilter"/>, it
    /// therefore holds back the tail that may still grow into a known string, and releases it as soon as
    /// the next chunk proves it will not.
    /// </para>
    /// </summary>
    public sealed class ToolResultEchoStreamFilter
    {
        /// <summary>
        /// Shortest engine string worth matching. Short results ("ok", "3", "true") are ordinary words
        /// that a teacher legitimately says; removing those by identity would still be removing text the
        /// model meant. Only a payload long enough to be unmistakably ours is stripped.
        /// </summary>
        public const int MinNeedleLength = 24;

        private readonly List<string> _needles = new();
        private readonly StringBuilder _pending = new();

        /// <summary>Whether at least one verbatim repetition was removed from the visible stream.</summary>
        public bool StrippedEcho { get; private set; }

        /// <summary>Whether any engine string is registered; nothing can be stripped without one.</summary>
        public bool HasNeedles => _needles.Count > 0;

        /// <summary>Drops held text and every registered string.</summary>
        public void Reset()
        {
            _needles.Clear();
            _pending.Clear();
            StrippedEcho = false;
        }

        /// <summary>
        /// Registers the results of executed tool calls as strings the model is not allowed to repeat
        /// word for word. Safe to call repeatedly with a growing list.
        /// </summary>
        public void RegisterToolResults(IReadOnlyList<LlmToolCallTrace> traces)
        {
            if (traces == null)
            {
                return;
            }

            for (int i = 0; i < traces.Count; i++)
            {
                RegisterEngineText(traces[i].Detail);
            }
        }

        /// <summary>
        /// Registers one exact engine-produced string. Both the raw text and its newline-normalized form
        /// are kept: a tool may return CRLF while the model repeats it with bare LF, and that difference
        /// alone must not let the repetition through.
        /// </summary>
        public void RegisterEngineText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            AddNeedle(text);
            AddNeedle(text.Trim());
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            AddNeedle(normalized);
            AddNeedle(normalized.Trim());
        }

        /// <summary>
        /// Processes one streaming chunk and returns only text that is safe to display.
        /// </summary>
        public string ProcessChunk(string chunk)
        {
            if (!string.IsNullOrEmpty(chunk))
            {
                _pending.Append(chunk);
            }

            if (_pending.Length == 0)
            {
                return string.Empty;
            }

            if (_needles.Count == 0)
            {
                return TakePending();
            }

            string buffer = _pending.ToString();
            StringBuilder visible = new(buffer.Length);
            int scan = 0;
            while (scan < buffer.Length && TryFindEarliestNeedle(buffer, scan, out int at, out int length))
            {
                visible.Append(buffer, scan, at - scan);
                scan = at + length;
                StrippedEcho = true;
            }

            string rest = buffer.Substring(scan);
            int held = ComputeHeldLength(rest);
            visible.Append(rest, 0, rest.Length - held);
            _pending.Clear();
            _pending.Append(rest, rest.Length - held, held);
            return visible.ToString();
        }

        /// <summary>Returns the held tail at the end of a stream; it can no longer become a repetition.</summary>
        public string Flush()
        {
            return TakePending();
        }

        /// <summary>
        /// One-shot form for the non-streaming path and for the text that is persisted and published.
        /// Returns the input unchanged when there is nothing registered to match.
        /// </summary>
        public static string StripEchoedToolResults(string content, IReadOnlyList<LlmToolCallTrace> traces)
        {
            if (string.IsNullOrEmpty(content) || traces == null || traces.Count == 0)
            {
                return content;
            }

            ToolResultEchoStreamFilter filter = new();
            filter.RegisterToolResults(traces);
            if (!filter.HasNeedles)
            {
                return content;
            }

            string stripped = filter.ProcessChunk(content) + filter.Flush();
            return filter.StrippedEcho ? stripped : content;
        }

        private void AddNeedle(string candidate)
        {
            if (string.IsNullOrEmpty(candidate) || candidate.Length < MinNeedleLength)
            {
                return;
            }

            for (int i = 0; i < _needles.Count; i++)
            {
                if (string.Equals(_needles[i], candidate, StringComparison.Ordinal))
                {
                    return;
                }
            }

            _needles.Add(candidate);
        }

        private string TakePending()
        {
            string pending = _pending.ToString();
            _pending.Clear();
            return pending;
        }

        /// <summary>
        /// Finds the first registered string at or after <paramref name="from"/>. On ties the LONGEST
        /// match wins, so a result that fully contains a shorter one is removed whole.
        /// </summary>
        private bool TryFindEarliestNeedle(string buffer, int from, out int at, out int length)
        {
            at = -1;
            length = 0;
            for (int i = 0; i < _needles.Count; i++)
            {
                string needle = _needles[i];
                int index = buffer.IndexOf(needle, from, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }

                if (at < 0 || index < at || (index == at && needle.Length > length))
                {
                    at = index;
                    length = needle.Length;
                }
            }

            return at >= 0;
        }

        /// <summary>
        /// Length of the trailing text that is still a proper prefix of some registered string, i.e. the
        /// part that must not be shown yet because the next chunk may complete a repetition.
        /// <para>
        /// There is deliberately NO ceiling on this. A ceiling looks like a safety valve and is the
        /// opposite: capping the hold below the length of a registered string means a repetition of THAT
        /// string can never complete across a chunk boundary, so the whole payload leaks — precisely the
        /// long results (a JSON envelope, a file dump) where leaking hurts most, and only under the
        /// chunk split the provider happened to choose, which is why it would be unreproducible.
        /// The hold is already bounded: it can never exceed the longest registered string, and those are
        /// strings this engine itself produced and is holding in memory anyway. The stream only stalls
        /// while the model is literally reproducing a tool result, and <see cref="Flush"/> releases the
        /// tail at end of turn, so nothing is ever lost.
        /// </para>
        /// </summary>
        private int ComputeHeldLength(string rest)
        {
            int held = 0;
            for (int i = 0; i < _needles.Count; i++)
            {
                string needle = _needles[i];
                int maxHold = Math.Min(rest.Length, needle.Length - 1);
                for (int keep = maxHold; keep > held; keep--)
                {
                    if (string.CompareOrdinal(rest, rest.Length - keep, needle, 0, keep) == 0)
                    {
                        held = keep;
                        break;
                    }
                }
            }

            return held;
        }
    }
}
