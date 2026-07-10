using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>How <see cref="WordlistContentFilter"/> reacts when a blocked term matches.</summary>
    public enum ContentFilterMode
    {
        /// <summary>Replace each matched term with asterisks of the same length and allow the rest.</summary>
        RedactTerms = 0,

        /// <summary>Return a Block verdict for the whole message when any term matches.</summary>
        BlockMessage = 1
    }

    /// <summary>
    /// Baseline <see cref="IContentFilter"/> over a caller-supplied blocked-term list.
    /// <para>
    /// Matching is case-insensitive ordinal (<see cref="StringComparison.OrdinalIgnoreCase"/>) and
    /// whole-word-ish: a term only matches when its neighbours are not letters or digits, so "class"
    /// never trips a three-letter term inside it. Implemented as a plain per-term
    /// <see cref="string.IndexOf(string, int, StringComparison)"/> scan rather than a precompiled
    /// alternation regex: it needs no escaping of user-supplied terms, works on any Unicode script
    /// (including Cyrillic) via ordinal case folding, and allocates nothing when no term matches —
    /// the Allow path is zero-allocation.
    /// </para>
    /// <para>
    /// CoreAI ships NO default profanity list — this type is mechanism, not policy. The host supplies
    /// the terms; an empty/null wordlist behaves exactly like <see cref="PassthroughContentFilter"/>.
    /// Wordlists are a baseline only: real deployments (education, consoles) should implement
    /// <see cref="IContentFilter"/> over a proper moderation model or service instead.
    /// </para>
    /// Immutable after construction and therefore thread-safe.
    /// </summary>
    public sealed class WordlistContentFilter : IContentFilter
    {
        private readonly string[] _terms;
        private readonly ContentFilterMode _mode;

        /// <summary>
        /// Creates a filter over <paramref name="blockedTerms"/>. Terms are trimmed; null/whitespace
        /// entries are dropped. A null or effectively empty list yields passthrough behavior.
        /// </summary>
        public WordlistContentFilter(IEnumerable<string> blockedTerms, ContentFilterMode mode)
        {
            _mode = mode;

            if (blockedTerms == null)
            {
                _terms = Array.Empty<string>();
                return;
            }

            List<string> cleaned = new();
            foreach (string term in blockedTerms)
            {
                string trimmed = term?.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    cleaned.Add(trimmed);
                }
            }

            _terms = cleaned.ToArray();
        }

        /// <inheritdoc />
        public ContentFilterVerdict Evaluate(string text, ContentFilterContext context)
        {
            if (string.IsNullOrEmpty(text) || _terms.Length == 0)
            {
                return ContentFilterVerdict.Allow;
            }

            if (_mode == ContentFilterMode.BlockMessage)
            {
                for (int t = 0; t < _terms.Length; t++)
                {
                    if (FindMatch(text, _terms[t], 0) >= 0)
                    {
                        return ContentFilterVerdict.Block($"Blocked term '{_terms[t]}' matched.");
                    }
                }

                return ContentFilterVerdict.Allow;
            }

            // RedactTerms: the buffer is created lazily on the FIRST match so the Allow path
            // (no term present) never allocates.
            char[] buffer = null;
            for (int t = 0; t < _terms.Length; t++)
            {
                string term = _terms[t];
                int searchFrom = 0;
                int index;
                while ((index = FindMatch(text, term, searchFrom)) >= 0)
                {
                    buffer ??= text.ToCharArray();
                    for (int i = index; i < index + term.Length; i++)
                    {
                        buffer[i] = '*';
                    }

                    searchFrom = index + term.Length;
                }
            }

            return buffer == null
                ? ContentFilterVerdict.Allow
                : ContentFilterVerdict.Redact(new string(buffer), "Blocked terms redacted.");
        }

        /// <summary>
        /// Index of the next whole-word-ish, case-insensitive occurrence of <paramref name="term"/>
        /// at or after <paramref name="startIndex"/>, or -1. A boundary is the string edge or any
        /// character that is not a Unicode letter or digit.
        /// </summary>
        private static int FindMatch(string text, string term, int startIndex)
        {
            for (int from = startIndex; from <= text.Length - term.Length;)
            {
                int index = text.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    return -1;
                }

                bool leftBoundary = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
                int end = index + term.Length;
                bool rightBoundary = end == text.Length || !char.IsLetterOrDigit(text[end]);
                if (leftBoundary && rightBoundary)
                {
                    return index;
                }

                from = index + 1;
            }

            return -1;
        }
    }
}
