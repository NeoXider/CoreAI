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

        /// <summary>Clears the global CoreAI facade registrations.</summary>
        public void Reset()
        {
            _buffer.Clear();
            _insideThink = false;
        }

        /// <summary>
/// Executes ProcessChunk API operation.
        ///
        ///
        /// </summary>
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
                        _insideThink = false;
                        buf = buf.Substring(closeIdx + CloseTag.Length);
                    }
                    else
                    {
                        /* Implementation note in English. */
                        /* Implementation note in English. */
                        _buffer.Clear();
                        _buffer.Append(KeepTailForPossibleTag(buf, CloseTag));
                        return visible.ToString();
                    }
                }
                else
                {
                    int openIdx = buf.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
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
                        /* Implementation note in English. */
                        int lastLt = buf.LastIndexOf('<');
                        if (lastLt >= 0)
                        {
                            string possibleTag = buf.Substring(lastLt);
                            if (IsPrefixOf(possibleTag, OpenTag))
                            {
                                if (lastLt > 0)
                                {
                                    visible.Append(buf, 0, lastLt);
                                }

                                _buffer.Clear();
                                _buffer.Append(possibleTag);
                                return visible.ToString();
                            }
                        }

                        visible.Append(buf);
                        buf = string.Empty;
                    }
                }
            }

            _buffer.Clear();
            return visible.ToString();
        }

        /// <summary>
/// Executes Flush API operation.
        ///
        ///
        ///
        /// </summary>
        public string Flush()
        {
            if (_insideThink)
            {
                _buffer.Clear();
                return string.Empty;
            }

            if (_buffer.Length == 0)
            {
                return string.Empty;
            }

            string tail = _buffer.ToString();
            _buffer.Clear();

            /* Implementation note in English. */
            /* Implementation note in English. */
            return IsPrefixOf(tail, OpenTag) ? string.Empty : tail;
        }

        /// <summary>
/// Executes KeepTailForPossibleTag API operation.
        ///
        ///
        ///
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
/// Executes IsPrefixOf API operation.
        ///
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
