using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CoreAI.Ai
{
    /// <summary>
    /// Loaded byte-level BPE encoder for a single encoding (cl100k_base or o200k_base). Implements
    /// the standard tiktoken algorithm: regex pre-tokenization into pieces, UTF-8 byte expansion,
    /// then greedy byte-pair merging by rank. Counting tokens only requires the merge ranks (no
    /// reverse vocabulary), so this class stores ranks keyed by byte-sequence.
    /// </summary>
    /// <remarks>
    /// AOT/IL2CPP safe: no reflection, no dynamic codegen. Ranks are parsed from a stream once and
    /// held in a dictionary. Special tokens (e.g. &lt;|endoftext|&gt;) are matched literally and each
    /// counts as exactly one token.
    /// </remarks>
    public sealed class BpeEncoder
    {
        // WHY: tiktoken pre-tokenization regexes. cl100k_base and o200k_base use different patterns.
        // These are the canonical patterns; .NET regex supports the needed constructs.
        private const string Cl100kPattern =
            @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+";

        private const string O200kPattern =
            @"[^\r\n\p{L}\p{N}]?[\p{L}\p{M}]+|[^\r\n\p{L}\p{N}]?[\p{N}]{1,3}| ?[^\s\p{L}\p{N}]+[\r\n/]*|\s*[\r\n]+|\s+(?!\S)|\s+";

        private readonly Regex _pattern;
        private readonly Dictionary<ByteSeq, int> _ranks;
        private readonly Dictionary<string, int> _specialTokens;
        private readonly Regex _specialPattern;

        private BpeEncoder(Regex pattern, Dictionary<ByteSeq, int> ranks, Dictionary<string, int> specialTokens)
        {
            _pattern = pattern;
            _ranks = ranks;
            _specialTokens = specialTokens;
            _specialPattern = BuildSpecialPattern(specialTokens);
        }

        /// <summary>
        /// Parses a tiktoken rank stream into an encoder. Returns null on any parse/load failure so
        /// callers can fall back. The stream format is one "base64token rank" entry per line.
        /// </summary>
        /// <param name="encoding">Which encoding's pre-tokenization regex to use.</param>
        /// <param name="ranksStream">Open, readable rank stream. Disposed by the caller.</param>
        /// <param name="specialTokens">
        /// Optional special-token to rank map. When null, the standard &lt;|endoftext|&gt; is used.
        /// </param>
        public static BpeEncoder TryLoad(
            BpeEncoding encoding,
            Stream ranksStream,
            IReadOnlyDictionary<string, int> specialTokens = null)
        {
            if (ranksStream == null)
            {
                return null;
            }

            string patternText = encoding == BpeEncoding.O200kBase ? O200kPattern : Cl100kPattern;

            Dictionary<ByteSeq, int> ranks;
            try
            {
                ranks = ParseRanks(ranksStream);
            }
            catch
            {
                return null;
            }

            if (ranks == null || ranks.Count == 0)
            {
                return null;
            }

            Regex pattern;
            try
            {
                pattern = new Regex(patternText, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            }
            catch
            {
                // WHY: RegexOptions.Compiled is unsupported on some AOT/WebGL targets: retry interpreted.
                try
                {
                    pattern = new Regex(patternText, RegexOptions.CultureInvariant);
                }
                catch
                {
                    return null;
                }
            }

            Dictionary<string, int> special = new(StringComparer.Ordinal);
            if (specialTokens != null)
            {
                foreach (KeyValuePair<string, int> kv in specialTokens)
                {
                    if (!string.IsNullOrEmpty(kv.Key))
                    {
                        special[kv.Key] = kv.Value;
                    }
                }
            }
            else
            {
                special["<|endoftext|>"] = ranks.Count;
            }

            return new BpeEncoder(pattern, ranks, special);
        }

        /// <summary>
        /// Counts the tokens in <paramref name="text"/>. Special tokens (if present literally) count
        /// as one token each; everything between them is regex-split and byte-pair encoded.
        /// </summary>
        public int CountTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int total = 0;
            int cursor = 0;

            while (cursor < text.Length)
            {
                int nextSpecial = -1;
                int specialLen = 0;
                if (_specialPattern != null)
                {
                    Match m = _specialPattern.Match(text, cursor);
                    if (m.Success)
                    {
                        nextSpecial = m.Index;
                        specialLen = m.Length;
                    }
                }

                int gapEnd = nextSpecial >= 0 ? nextSpecial : text.Length;
                if (gapEnd > cursor)
                {
                    total += CountOrdinary(text, cursor, gapEnd - cursor);
                }

                if (nextSpecial < 0)
                {
                    break;
                }

                total += 1; // one special token
                cursor = nextSpecial + specialLen;
            }

            return total;
        }

        private int CountOrdinary(string text, int start, int length)
        {
            int total = 0;
            string slice = text.Substring(start, length);
            foreach (Match piece in _pattern.Matches(slice))
            {
                if (piece.Length == 0)
                {
                    continue;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(piece.Value);
                total += CountPiece(bytes);
            }

            return total;
        }

        /// <summary>
        /// Byte-pair-merge for a single pre-token. Returns the number of resulting tokens. This is
        /// the standard tiktoken merge loop: repeatedly merge the adjacent pair with the lowest rank
        /// until no mergeable pair remains. Token COUNT equals the number of segments at the end.
        /// </summary>
        private int CountPiece(byte[] piece)
        {
            int n = piece.Length;
            if (n == 0)
            {
                return 0;
            }

            // WHY: A single byte is always a known token in byte-level BPE.
            if (n == 1)
            {
                return 1;
            }

            // WHY: Segment boundaries: parts[i] is the start index of segment i; there are (count) segments
            // covering [parts[i], parts[i+1]). Initialize to one byte per segment.
            List<int> starts = new(n + 1);
            for (int i = 0; i <= n; i++)
            {
                starts.Add(i);
            }

            while (starts.Count > 2)
            {
                int bestRank = int.MaxValue;
                int bestIdx = -1;

                for (int i = 0; i < starts.Count - 2; i++)
                {
                    int rank = RankOf(piece, starts[i], starts[i + 2]);
                    if (rank >= 0 && rank < bestRank)
                    {
                        bestRank = rank;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0)
                {
                    break; // no further merges possible
                }

                starts.RemoveAt(bestIdx + 1);
            }

            return starts.Count - 1;
        }

        private int RankOf(byte[] piece, int start, int end)
        {
            ByteSeq key = new(piece, start, end);
            return _ranks.TryGetValue(key, out int rank) ? rank : -1;
        }

        private static Dictionary<ByteSeq, int> ParseRanks(Stream stream)
        {
            Dictionary<ByteSeq, int> ranks = new(ByteSeqComparer.Instance);
            using StreamReader reader = new(stream, Encoding.UTF8, true, 1 << 16, true);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                int sp = line.IndexOf(' ');
                if (sp <= 0 || sp >= line.Length - 1)
                {
                    continue;
                }

                string b64 = line.Substring(0, sp);
                string rankText = line.Substring(sp + 1);
                if (!int.TryParse(rankText, out int rank))
                {
                    continue;
                }

                byte[] tokenBytes;
                try
                {
                    tokenBytes = Convert.FromBase64String(b64);
                }
                catch
                {
                    continue;
                }

                if (tokenBytes.Length == 0)
                {
                    continue;
                }

                ranks[new ByteSeq(tokenBytes, 0, tokenBytes.Length)] = rank;
            }

            return ranks;
        }

        private static Regex BuildSpecialPattern(Dictionary<string, int> specialTokens)
        {
            if (specialTokens == null || specialTokens.Count == 0)
            {
                return null;
            }

            StringBuilder sb = new();
            bool first = true;
            foreach (string token in specialTokens.Keys)
            {
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }

                if (!first)
                {
                    sb.Append('|');
                }

                sb.Append(Regex.Escape(token));
                first = false;
            }

            if (first)
            {
                return null;
            }

            try
            {
                return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Lightweight view over a byte slice used as a dictionary key without per-lookup allocation
        /// of a copied array (the backing array is shared; equality compares contents).
        /// </summary>
        private readonly struct ByteSeq
        {
            private readonly byte[] _buffer;
            private readonly int _start;
            private readonly int _length;

            public ByteSeq(byte[] buffer, int start, int end)
            {
                _buffer = buffer;
                _start = start;
                _length = end - start;
            }

            public int Length => _length;

            public byte At(int i)
            {
                return _buffer[_start + i];
            }

            public int ComputeHash()
            {
                // WHY: FNV-1a over the slice contents.
                unchecked
                {
                    const int prime = 16777619;
                    int hash = (int)2166136261;
                    for (int i = 0; i < _length; i++)
                    {
                        hash = (hash ^ _buffer[_start + i]) * prime;
                    }

                    return hash;
                }
            }

            public bool ContentEquals(in ByteSeq other)
            {
                if (_length != other._length)
                {
                    return false;
                }

                for (int i = 0; i < _length; i++)
                {
                    if (_buffer[_start + i] != other._buffer[other._start + i])
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private sealed class ByteSeqComparer : IEqualityComparer<ByteSeq>
        {
            public static readonly ByteSeqComparer Instance = new();

            public bool Equals(ByteSeq x, ByteSeq y)
            {
                return x.ContentEquals(in y);
            }

            public int GetHashCode(ByteSeq obj)
            {
                return obj.ComputeHash();
            }
        }
    }
}
