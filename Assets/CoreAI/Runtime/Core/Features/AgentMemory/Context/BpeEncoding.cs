namespace CoreAI.Ai
{
    /// <summary>
    /// Known byte-level BPE encodings used by OpenAI-family models.
    /// </summary>
    public enum BpeEncoding
    {
        /// <summary>Model is not recognized; callers must fall back to the heuristic estimator.</summary>
        Unknown = 0,

        /// <summary>cl100k_base — gpt-4, gpt-3.5-turbo, text-embedding-3/ada-002.</summary>
        Cl100kBase = 1,

        /// <summary>o200k_base — gpt-4o, gpt-4.1, o1/o3/o4 family.</summary>
        O200kBase = 2
    }

    /// <summary>
    /// Resolves a <see cref="BpeEncoding"/> from a provider model id by prefix matching, mirroring
    /// the model-to-encoding map used by tiktoken. Unknown models resolve to
    /// <see cref="BpeEncoding.Unknown"/> so the counter falls back to the estimator.
    /// </summary>
    public static class BpeEncodingResolver
    {
        /// <summary>
        /// Maps a model name to its encoding. Matching is case-insensitive and prefix-based; the
        /// o200k family is checked first because some ids (e.g. "gpt-4o") are prefixes of cl100k ids
        /// (e.g. "gpt-4") when compared the other way around.
        /// </summary>
        public static BpeEncoding Resolve(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return BpeEncoding.Unknown;
            }

            string m = modelName.Trim().ToLowerInvariant();

            // o200k_base family (newer). Check before cl100k so "gpt-4o*" does not match "gpt-4".
            if (StartsWithAny(m,
                    "gpt-4o", "gpt-4.1", "gpt-4.5", "gpt-5",
                    "o1", "o3", "o4",
                    "chatgpt-4o", "o200k"))
            {
                return BpeEncoding.O200kBase;
            }

            // cl100k_base family.
            if (StartsWithAny(m,
                    "gpt-4", "gpt-3.5", "gpt-35",
                    "text-embedding-3", "text-embedding-ada-002",
                    "cl100k"))
            {
                return BpeEncoding.Cl100kBase;
            }

            return BpeEncoding.Unknown;
        }

        private static bool StartsWithAny(string value, params string[] prefixes)
        {
            for (int i = 0; i < prefixes.Length; i++)
            {
                if (value.StartsWith(prefixes[i], System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}