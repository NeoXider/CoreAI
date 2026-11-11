using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// The per-world request quota of <c>TextGenerator:GenerateTextAsync</c>: a sliding one-minute window. The mirror
    /// limits requests to 100 per minute; the host configures the number. Engine-free; the caller passes the clock.
    /// </summary>
    public sealed class RbxTextGenerationQuota
    {
        /// <summary>The mirror's documented starting limit.</summary>
        public const int MirrorRequestsPerMinute = 100;

        private const double WindowSeconds = 60d;

        private readonly Queue<double> _stamps = new();

        /// <summary>Creates the quota; 0 or less is unlimited.</summary>
        public RbxTextGenerationQuota(int requestsPerMinute = MirrorRequestsPerMinute)
        {
            RequestsPerMinute = requestsPerMinute;
        }

        /// <summary>Requests allowed per minute; 0 or less is unlimited.</summary>
        public int RequestsPerMinute { get; }

        /// <summary>Takes one request at <paramref name="nowSeconds"/>; false (with the wait) when the window is full.</summary>
        public bool TryTake(double nowSeconds, out double retryAfterSeconds)
        {
            retryAfterSeconds = 0d;
            if (RequestsPerMinute <= 0)
            {
                return true;
            }

            while (_stamps.Count > 0 && nowSeconds - _stamps.Peek() >= WindowSeconds)
            {
                _stamps.Dequeue();
            }

            if (_stamps.Count >= RequestsPerMinute)
            {
                retryAfterSeconds = Math.Max(0d, WindowSeconds - (nowSeconds - _stamps.Peek()));
                return false;
            }

            _stamps.Enqueue(nowSeconds);
            return true;
        }

        /// <summary>Gives back the newest request taken (the request was refused before it reached the model).</summary>
        public void Release()
        {
            if (_stamps.Count == 0)
            {
                return;
            }

            double[] kept = _stamps.ToArray();
            _stamps.Clear();
            for (int index = 0; index < kept.Length - 1; index++)
            {
                _stamps.Enqueue(kept[index]);
            }
        }
    }

    /// <summary>One exchange of a <c>TextGenerator</c> conversation.</summary>
    public readonly struct RbxTextGenerationTurn
    {
        /// <summary>Creates the exchange.</summary>
        public RbxTextGenerationTurn(string userPrompt, string generatedText)
        {
            UserPrompt = userPrompt ?? "";
            GeneratedText = generatedText ?? "";
        }

        /// <summary>What the script asked.</summary>
        public string UserPrompt { get; }

        /// <summary>What the model answered.</summary>
        public string GeneratedText { get; }
    }

    /// <summary>
    /// The conversations behind <c>ContextToken</c>: the token a script gets back is an opaque handle to the exchanges
    /// so far, kept host-side. Every answer mints a new token for the conversation including it, so passing an older
    /// token continues from that point (the mirror: "providing a previously generated context token restores the
    /// conversation state"). Bounded: at most <see cref="MaxConversations"/> tokens (least recently used dropped) and
    /// <see cref="MaxTurns"/> exchanges per token (oldest dropped). Memory only: tokens do not survive a restart.
    /// </summary>
    public sealed class RbxTextGenerationContexts
    {
        /// <summary>Prefix of every token, so a log line shows what it is.</summary>
        public const string TokenPrefix = "coreai-ctx-";

        private readonly Dictionary<string, LinkedListNode<Entry>> _byToken = new(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _recent = new();
        private long _nextId;

        /// <summary>Creates the store.</summary>
        public RbxTextGenerationContexts(int maxConversations = 256, int maxTurns = 16)
        {
            MaxConversations = Math.Max(1, maxConversations);
            MaxTurns = Math.Max(1, maxTurns);
        }

        /// <summary>Most tokens kept.</summary>
        public int MaxConversations { get; }

        /// <summary>Most exchanges kept per token.</summary>
        public int MaxTurns { get; }

        /// <summary>Tokens held now.</summary>
        public int Count => _byToken.Count;

        /// <summary>
        /// The exchanges behind <paramref name="token"/>; false when the token is unknown (never minted here, or
        /// dropped). An empty or null token is a new conversation and answers true with no exchanges.
        /// </summary>
        public bool TryGet(string token, out IReadOnlyList<RbxTextGenerationTurn> turns)
        {
            if (string.IsNullOrEmpty(token))
            {
                turns = Array.Empty<RbxTextGenerationTurn>();
                return true;
            }

            if (!_byToken.TryGetValue(token, out LinkedListNode<Entry> node))
            {
                turns = Array.Empty<RbxTextGenerationTurn>();
                return false;
            }

            _recent.Remove(node);
            _recent.AddFirst(node);
            turns = node.Value.Turns;
            return true;
        }

        /// <summary>Mints the token of <paramref name="previous"/> followed by one more exchange.</summary>
        public string Append(IReadOnlyList<RbxTextGenerationTurn> previous, RbxTextGenerationTurn turn)
        {
            List<RbxTextGenerationTurn> turns = new((previous?.Count ?? 0) + 1);
            if (previous != null)
            {
                int skip = Math.Max(0, previous.Count + 1 - MaxTurns);
                for (int index = skip; index < previous.Count; index++)
                {
                    turns.Add(previous[index]);
                }
            }

            turns.Add(turn);
            string token = TokenPrefix + (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" +
                           Guid.NewGuid().ToString("N").Substring(0, 12);
            LinkedListNode<Entry> node = _recent.AddFirst(new Entry(token, turns));
            _byToken[token] = node;
            while (_byToken.Count > MaxConversations && _recent.Last != null)
            {
                _byToken.Remove(_recent.Last.Value.Token);
                _recent.RemoveLast();
            }

            return token;
        }

        private sealed class Entry
        {
            public Entry(string token, IReadOnlyList<RbxTextGenerationTurn> turns)
            {
                Token = token;
                Turns = turns;
            }

            public string Token { get; }

            public IReadOnlyList<RbxTextGenerationTurn> Turns { get; }
        }
    }
}
