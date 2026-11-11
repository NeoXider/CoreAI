using System;
using System.Text;

namespace CoreAI.RuntimeUi
{
    /// <summary>Why a <c>var()</c> substitution failed.</summary>
    public enum UssVarFailure
    {
        /// <summary>Nothing failed.</summary>
        None = 0,

        /// <summary>A custom property is undefined and the <c>var()</c> has no fallback.</summary>
        Unresolved,

        /// <summary>The <c>var()</c> call is not well formed.</summary>
        Malformed,

        /// <summary>The substitution nests deeper than the cycle guard allows.</summary>
        TooDeep
    }

    /// <summary>Replaces <c>var(--name)</c> and <c>var(--name, fallback)</c> with custom property values.</summary>
    public static class UssVarSubstitution
    {
        private const int MaxDepth = 16;

        /// <summary>True when the text contains a <c>var(</c> call.</summary>
        public static bool ContainsVar(string text)
        {
            return text != null && text.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Substitutes every <c>var()</c> in <paramref name="text"/>.</summary>
        /// <param name="text">Value text.</param>
        /// <param name="properties">The custom properties in scope; may be null.</param>
        /// <param name="result">The substituted text.</param>
        /// <param name="failure">Why it failed.</param>
        /// <param name="failedName">The custom property that could not be resolved.</param>
        public static bool TrySubstitute(
            string text,
            UssCustomProperties properties,
            out string result,
            out UssVarFailure failure,
            out string failedName)
        {
            return TrySubstitute(text, properties, 0, out result, out failure, out failedName);
        }

        private static bool TrySubstitute(
            string text,
            UssCustomProperties properties,
            int depth,
            out string result,
            out UssVarFailure failure,
            out string failedName)
        {
            failure = UssVarFailure.None;
            failedName = null;
            result = text;
            if (!ContainsVar(text))
            {
                return true;
            }

            if (depth > MaxDepth)
            {
                failure = UssVarFailure.TooDeep;
                return false;
            }

            StringBuilder builder = new(text.Length + 16);
            int i = 0;
            while (i < text.Length)
            {
                int start = text.IndexOf("var(", i, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    builder.Append(text, i, text.Length - i);
                    break;
                }

                builder.Append(text, i, start - i);
                int close = FindClosingParenthesis(text, start + 3);
                if (close < 0)
                {
                    failure = UssVarFailure.Malformed;
                    return false;
                }

                string inner = text.Substring(start + 4, close - start - 4);
                int comma = TopLevelComma(inner);
                string name = (comma >= 0 ? inner.Substring(0, comma) : inner).Trim();
                string fallback = comma >= 0 ? inner.Substring(comma + 1).Trim() : null;
                if (!UssPropertyCatalog.IsCustomProperty(name))
                {
                    failure = UssVarFailure.Malformed;
                    failedName = name;
                    return false;
                }

                string replacement;
                if (properties != null && properties.TryGet(name, out string value))
                {
                    replacement = value;
                }
                else if (fallback != null)
                {
                    if (!TrySubstitute(fallback, properties, depth + 1, out replacement, out failure, out failedName))
                    {
                        return false;
                    }
                }
                else
                {
                    failure = UssVarFailure.Unresolved;
                    failedName = name;
                    return false;
                }

                builder.Append(replacement);
                i = close + 1;
            }

            result = builder.ToString();
            if (ContainsVar(result))
            {
                return TrySubstitute(result, properties, depth + 1, out result, out failure, out failedName);
            }

            return true;
        }

        private static int FindClosingParenthesis(string text, int openIndex)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = openIndex; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static int TopLevelComma(string text)
        {
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
