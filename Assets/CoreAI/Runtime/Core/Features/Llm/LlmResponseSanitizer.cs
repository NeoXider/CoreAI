using System;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Some chat models occasionally degenerate into repeating the system prompt in the assistant
    /// channel (especially when output max tokens are large). Detect a long shared prefix with
    /// the actual system string and strip it so the user does not see a wall of instructions.
    /// </summary>
    public static class LlmResponseSanitizer
    {
        /// <summary>
        /// Removes one or more leading copies of <paramref name="systemPrompt"/> from
        /// <paramref name="content"/> when the matched prefix is at least
        /// <paramref name="minPrefixChars"/> characters (and the system prompt is long enough).
        /// </summary>
        public static string StripLeadingSystemPromptEcho(
            string? content,
            string? systemPrompt,
            int minPrefixChars = 200,
            int maxIterations = 5)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(systemPrompt))
            {
                return content ?? string.Empty;
            }

            string c = content;
            string s = systemPrompt;

            for (int iter = 0; iter < maxIterations; iter++)
            {
                ReadOnlySpan<char> ct = c.AsSpan().TrimStart();
                ReadOnlySpan<char> st = s.AsSpan().TrimStart();
                if (ct.Length == 0 || st.Length == 0)
                {
                    break;
                }

                int prefix = CommonPrefixLength(ct, st);
                int threshold = Math.Min(minPrefixChars, st.Length);
                if (prefix < threshold)
                {
                    break;
                }

                // Map prefix from trimmed view back to start index in original `c` (skip leading whitespace once).
                int leadingWs = c.AsSpan().Length - ct.Length;
                c = c.Substring(leadingWs + prefix).TrimStart();
                if (c.Length == 0)
                {
                    return string.Empty;
                }
            }

            return c;
        }

        private static int CommonPrefixLength(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        {
            int n = Math.Min(a.Length, b.Length);
            int i = 0;
            for (; i < n; i++)
            {
                if (a[i] != b[i])
                {
                    break;
                }
            }

            return i;
        }
    }
}