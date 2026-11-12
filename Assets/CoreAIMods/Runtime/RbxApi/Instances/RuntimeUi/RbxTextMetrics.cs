using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// The size of text when no host measures it (a headless world, a host element that cannot): every character
    /// advances half an em, a line is <c>TextSize</c> times <c>LineHeight</c> high, and a wrapped line breaks at the
    /// last space that fits, or mid-word when a word alone is wider than the box. Deterministic and allocation-free,
    /// so tests and servers lay text out the same way every time; the Unity host measures with the real font instead.
    /// </summary>
    public static class RbxTextMetrics
    {
        /// <summary>Average advance of a character, in ems.</summary>
        public const float AdvanceEm = 0.5f;

        /// <summary>
        /// The size of <paramref name="text"/> at <paramref name="fontSize"/>; <paramref name="maxWidth"/> above 0 wraps
        /// it at that width. Empty text is 0 wide and one line high, as a label's caret line is.
        /// </summary>
        public static RbxVector2 Estimate(string text, float fontSize, float maxWidth, float lineHeight)
        {
            float size = fontSize > 0f && !float.IsNaN(fontSize) ? fontSize : 0f;
            float advance = size * AdvanceEm;
            float line = size * (lineHeight > 0f && !float.IsNaN(lineHeight) ? lineHeight : 1f);
            string value = text ?? string.Empty;
            if (advance <= 0f)
            {
                return new RbxVector2(0f, line);
            }

            int perLine = maxWidth > 0f ? Math.Max(1, (int)Math.Floor(maxWidth / advance + 1e-4f)) : int.MaxValue;
            int lines = 0;
            int widest = 0;
            int start = 0;
            while (true)
            {
                int end = value.IndexOf('\n', start);
                int stop = end < 0 ? value.Length : end;
                CountParagraph(value, start, stop, perLine, ref lines, ref widest);
                if (end < 0)
                {
                    break;
                }

                start = end + 1;
            }

            return new RbxVector2(widest * advance, Math.Max(1, lines) * line);
        }

        private static void CountParagraph(string value, int start, int stop, int perLine, ref int lines,
            ref int widest)
        {
            int length = stop - start;
            if (length <= perLine)
            {
                lines++;
                widest = Math.Max(widest, length);
                return;
            }

            int lineStart = start;
            while (lineStart < stop)
            {
                int remaining = stop - lineStart;
                if (remaining <= perLine)
                {
                    lines++;
                    widest = Math.Max(widest, remaining);
                    return;
                }

                int breakAt = -1;
                for (int i = lineStart + perLine; i > lineStart; i--)
                {
                    if (value[i] == ' ')
                    {
                        breakAt = i;
                        break;
                    }
                }

                int lineEnd = breakAt > lineStart ? breakAt : lineStart + perLine;
                lines++;
                widest = Math.Max(widest, lineEnd - lineStart);
                lineStart = lineEnd;
                while (lineStart < stop && value[lineStart] == ' ')
                {
                    lineStart++;
                }
            }
        }
    }
}
