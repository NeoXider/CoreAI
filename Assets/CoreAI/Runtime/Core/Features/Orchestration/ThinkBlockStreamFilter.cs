using System;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Stateful stream filter that removes hidden <think> blocks from model output.
    /// </summary>
    public sealed class ThinkBlockStreamFilter
    {
        private const string OpenTag = "<think>";
        private const string CloseTag = "</think>";

        private readonly StringBuilder _buffer = new();
        private bool _insideThink;

        /// <summary>
        /// Whether at least one visible character of this stream has already been shown.
        /// <para>
        /// A lone <c>&lt;/think&gt;</c> with no opening tag hides the text in front of it only BEFORE the
        /// first visible output: models that stream their reasoning without a <c>&lt;think&gt;</c> do so at
        /// the start of the answer. Once text is already on screen there is nothing to hide - and the held
        /// remainder of a chunk must not be hidden either, otherwise the result depends on where the
        /// provider cut the stream: "Answer: yes." + " More &lt;" + "/think&gt;" lost the " More ", while
        /// the very same string delivered as one chunk did not.
        /// </para>
        /// </summary>
        private bool _visibleShown;

        /// <summary>
        /// Optional sink receiving the hidden reasoning text the moment it is suppressed from the
        /// visible stream, so hosts can show a live "thinking" section instead of losing the span.
        /// Null (the default) preserves the original suppress-only behavior.
        /// </summary>
        public Action<string> ReasoningSink { get; set; }

        /// <summary>Clears buffered partial tags and exits any active hidden-thought block.</summary>
        public void Reset()
        {
            _buffer.Clear();
            _insideThink = false;
            _visibleShown = false;
        }

        /// <summary>Forwards one suppressed reasoning span to <see cref="ReasoningSink"/>, if set.</summary>
        private void EmitReasoning(string hidden)
        {
            if (!string.IsNullOrEmpty(hidden))
            {
                ReasoningSink?.Invoke(hidden);
            }
        }

        /// <summary>
        /// Processes one streaming text chunk and returns only text that is safe to display.
        /// </summary>
        /// <remarks>
        /// The filter preserves partial <c>&lt;think&gt;</c> and <c>&lt;/think&gt;</c> tags across
        /// chunk boundaries so hidden reasoning is not leaked when providers split tokens mid-tag.
        /// A held tail that never turned into a tag is released by <see cref="Flush"/> - calling it at the
        /// end of the stream is mandatory, otherwise the answer "the comparison operator: &lt;" loses its
        /// last character.
        /// </remarks>
        public string ProcessChunk(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return string.Empty;
            }

            _buffer.Append(chunk);
            string buf = _buffer.ToString();
            StringBuilder visible = new();

            while (buf.Length > 0)
            {
                if (_insideThink)
                {
                    int closeIdx = buf.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
                    if (closeIdx >= 0)
                    {
                        EmitReasoning(buf.Substring(0, closeIdx));
                        _insideThink = false;
                        buf = buf.Substring(closeIdx + CloseTag.Length);
                    }
                    else
                    {
                        // WHY: Keep only a possible closing-tag prefix; all other hidden text stays suppressed.
                        string keptTail = KeepTailForPossibleTag(buf, CloseTag);
                        EmitReasoning(buf.Substring(0, buf.Length - keptTail.Length));
                        _buffer.Clear();
                        _buffer.Append(keptTail);
                        return Publish(visible);
                    }
                }
                else
                {
                    int openIdx = buf.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
                    int closeIdx = buf.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
                    if (closeIdx >= 0 && (openIdx < 0 || closeIdx < openIdx))
                    {
                        string beforeClose = buf.Substring(0, closeIdx);
                        if (_visibleShown || visible.Length > 0)
                        {
                            // WHY: visible text has already gone to the learner, so this is not reasoning
                            // but a stray tag: drop the tag itself, the text around it is a normal answer.
                            visible.Append(beforeClose);
                        }
                        else
                        {
                            // WHY: Some OpenAI-compatible reasoning models stream hidden thought text without
                            // the opening tag but still include </think> before the visible answer.
                            // Treat the buffered prefix as hidden and resume after the orphan close tag.
                            EmitReasoning(beforeClose);
                        }

                        buf = buf.Substring(closeIdx + CloseTag.Length);
                        continue;
                    }

                    if (openIdx >= 0)
                    {
                        if (openIdx > 0)
                        {
                            visible.Append(buf, 0, openIdx);
                        }

                        _insideThink = true;
                        buf = buf.Substring(openIdx + OpenTag.Length);
                    }
                    else
                    {
                        // WHY: Hold a possible tag until the next chunk proves whether it is real.
                        int lastLt = buf.LastIndexOf('<');
                        if (lastLt >= 0)
                        {
                            string possibleTag = buf.Substring(lastLt);
                            bool mayBecomeClose = IsPrefixOf(possibleTag, CloseTag);
                            bool mayBecomeOpen = IsPrefixOf(possibleTag, OpenTag);
                            if (mayBecomeClose && !_visibleShown && visible.Length == 0)
                            {
                                // WHY: nothing has been shown yet, and all the accumulated text may turn
                                // out to be reasoning in front of a lone </think> - hold all of it.
                                _buffer.Clear();
                                _buffer.Append(buf);
                                return string.Empty;
                            }

                            if (mayBecomeClose || mayBecomeOpen)
                            {
                                if (lastLt > 0)
                                {
                                    visible.Append(buf, 0, lastLt);
                                }

                                _buffer.Clear();
                                _buffer.Append(possibleTag);
                                return Publish(visible);
                            }
                        }

                        visible.Append(buf);
                        buf = string.Empty;
                    }
                }
            }

            _buffer.Clear();
            return Publish(visible);
        }

        /// <summary>
        /// Returns any buffered visible tail at the end of a stream.
        /// </summary>
        /// <remarks>
        /// The tail was held only because it COULD have become a tag. The stream ended and it did not, so
        /// it is ordinary answer text: a "2 &lt;" at the end of a Python teacher's reply is no worse than
        /// "2 &lt; 3". Hiding it as an "unfinished tag" means losing a legitimate character for the sake of
        /// a case that never happened.
        /// </remarks>
        public string Flush()
        {
            if (_insideThink)
            {
                // WHY: End-of-stream inside an unclosed think block: the buffered tail is still
                // hidden reasoning (never visible), so surface it to the sink before dropping it.
                EmitReasoning(_buffer.ToString());
                _buffer.Clear();
                return string.Empty;
            }

            string tail = _buffer.ToString();
            _buffer.Clear();
            if (tail.Length > 0)
            {
                _visibleShown = true;
            }

            return tail;
        }

        /// <summary>Releases the accumulated visible text and records that the learner has now seen it.</summary>
        private string Publish(StringBuilder visible)
        {
            if (visible.Length > 0)
            {
                _visibleShown = true;
            }

            return visible.ToString();
        }

        /// <summary>
        /// Keeps the longest suffix that may become the requested tag when the next chunk arrives.
        /// </summary>
        private static string KeepTailForPossibleTag(string buf, string tag)
        {
            int maxKeep = Math.Min(tag.Length - 1, buf.Length);
            for (int keep = maxKeep; keep > 0; keep--)
            {
                string tail = buf.Substring(buf.Length - keep);
                if (IsPrefixOf(tail, tag))
                {
                    return tail;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Case-insensitive ordinal prefix check for small protocol tags.
        /// </summary>
        private static bool IsPrefixOf(string candidate, string full)
        {
            if (string.IsNullOrEmpty(candidate) || candidate.Length > full.Length)
            {
                return false;
            }

            for (int i = 0; i < candidate.Length; i++)
            {
                char a = char.ToLowerInvariant(candidate[i]);
                char b = char.ToLowerInvariant(full[i]);
                if (a != b)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
