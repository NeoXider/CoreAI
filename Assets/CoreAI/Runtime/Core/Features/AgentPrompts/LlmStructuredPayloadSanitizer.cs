using System;

namespace CoreAI.Ai
{
    /// <summary>
    /// Strips markdown fences and extracts the first balanced JSON object from LLM text (legacy / heuristic parsers).
    /// Distinct from <c>CoreAI.Infrastructure.Llm.LlmResponseSanitizer</c> (system-prompt echo stripping).
    /// </summary>
    public static class LlmStructuredPayloadSanitizer
    {
        /// <summary>Removes one outer <c>```</c> / <c>```json</c> fence pair (one pass per edge).</summary>
        public static string StripMarkdownCodeFences(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return s;
            }

            string t = s.Trim();
            if (!t.StartsWith("```", StringComparison.Ordinal))
            {
                return t;
            }

            int afterOpen = t.IndexOf('\n');
            if (afterOpen < 0)
            {
                afterOpen = t.IndexOf('\r');
            }

            if (afterOpen >= 0)
            {
                t = t.Substring(afterOpen + 1).TrimStart('\r', '\n');
            }
            else
            {
                t = t.Substring(3).TrimStart();
            }

            int close = t.LastIndexOf("```", StringComparison.Ordinal);
            if (close >= 0)
            {
                t = t.Substring(0, close);
            }

            return t.Trim();
        }

        /// <summary>
        /// Strips repeated fences and returns the first balanced top-level <c>{ ... }</c> object (string-aware).
        /// </summary>
        public static bool TryPrepareJsonObject(string raw, out string jsonObject)
        {
            jsonObject = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            string work = raw.Trim();
            for (int pass = 0; pass < 6 && work.StartsWith("```", StringComparison.Ordinal); pass++)
            {
                work = StripMarkdownCodeFences(work);
            }

            return TryExtractFirstBalancedJsonObject(work, out jsonObject);
        }

        private static bool TryExtractFirstBalancedJsonObject(string s, out string json)
        {
            json = null;
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }

            int start = s.IndexOf('{');
            if (start < 0)
            {
                return false;
            }

            int depth = 0;
            bool inString = false;
            bool escape = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (inString)
                {
                    if (c == '\\')
                    {
                        escape = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        json = s.Substring(start, i - start + 1);
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
