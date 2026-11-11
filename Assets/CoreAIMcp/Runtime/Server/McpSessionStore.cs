using System;
using System.Collections.Generic;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Issues and validates <c>Mcp-Session-Id</c> values. Sessions are intentionally lightweight: the
    /// server is stateless behind the scenes, so a session id is a correlation token, never a security
    /// boundary (that is the bearer token in <see cref="McpRequestGuard"/>). Clients that never send one
    /// still work for POST; notification GET streams require a retained session.
    /// <para>
    /// WHY (bounded): every <c>initialize</c> issues a new id and a client that reconnects in a loop -
    /// which MCP clients do on every editor play/stop cycle - would grow this store without limit.
    /// Issuing prunes expired ids and enforces a hard cap, evicting the least recently used first.
    /// </para>
    /// <para>
    /// WHY (sliding): the lifetime counts from the last request that presented the id, not from
    /// <c>initialize</c>. An agent that works with the game for hours keeps its session; only a client
    /// that has been silent for the whole lifetime has to initialize again.
    /// </para>
    /// </summary>
    public sealed class McpSessionStore
    {
        /// <summary>How long a session id is retained after the last request that used it.</summary>
        public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromHours(1);

        /// <summary>Hard cap on retained session ids, regardless of TTL.</summary>
        public const int DefaultMaxSessions = 64;

        private readonly object _gate = new();
        private readonly Dictionary<string, DateTimeOffset> _lastUsed = new(StringComparer.Ordinal);
        private readonly TimeSpan _timeToLive;
        private readonly int _maxSessions;
        private readonly Func<DateTimeOffset> _clock;

        /// <param name="timeToLive">Idle lifetime of a session id; defaults to <see cref="DefaultTimeToLive"/>.</param>
        /// <param name="maxSessions">Hard cap; defaults to <see cref="DefaultMaxSessions"/>.</param>
        /// <param name="clock">Time source; defaults to UTC now. Injected by the TTL tests.</param>
        public McpSessionStore(TimeSpan? timeToLive = null, int maxSessions = DefaultMaxSessions,
            Func<DateTimeOffset> clock = null)
        {
            _timeToLive = timeToLive ?? DefaultTimeToLive;
            _maxSessions = maxSessions > 0 ? maxSessions : DefaultMaxSessions;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>Number of live sessions (diagnostics/tests).</summary>
        public int Count { get { lock (_gate) return _lastUsed.Count; } }

        /// <summary>Creates a fresh session id and records it, pruning expired and excess entries first.</summary>
        public string Issue()
        {
            lock (_gate)
            {
                DateTimeOffset now = _clock();
                Prune(now);
                string id = Guid.NewGuid().ToString("N");
                _lastUsed.Add(id, now);
                return id;
            }
        }

        /// <summary>
        /// True when the session has not expired or been evicted. A known session counts as used now, so
        /// its idle lifetime starts again.
        /// </summary>
        public bool IsKnown(string sessionId)
        {
            lock (_gate)
            {
                if (string.IsNullOrEmpty(sessionId) || !_lastUsed.TryGetValue(sessionId, out DateTimeOffset lastUsed))
                    return false;
                DateTimeOffset now = _clock();
                if (now - lastUsed <= _timeToLive)
                {
                    _lastUsed[sessionId] = now;
                    return true;
                }

                _lastUsed.Remove(sessionId);
                return false;
            }
        }

        /// <summary>Forgets a session (client sent HTTP DELETE). Returns false when it was not known.</summary>
        public bool Remove(string sessionId)
        {
            lock (_gate)
            {
                return !string.IsNullOrEmpty(sessionId) && _lastUsed.Remove(sessionId);
            }
        }

        private void Prune(DateTimeOffset now)
        {
            foreach (KeyValuePair<string, DateTimeOffset> entry in new List<KeyValuePair<string, DateTimeOffset>>(_lastUsed))
            {
                if (now - entry.Value > _timeToLive)
                {
                    _lastUsed.Remove(entry.Key);
                }
            }

            // WHY: a reconnect storm can outrun the TTL, so make room for the incoming id by dropping the
            // least recently used entries until the store is back under the cap.
            while (_lastUsed.Count >= _maxSessions)
            {
                string oldest = null;
                DateTimeOffset oldestAt = DateTimeOffset.MaxValue;
                foreach (KeyValuePair<string, DateTimeOffset> entry in _lastUsed)
                {
                    if (oldest == null || entry.Value < oldestAt)
                    {
                        oldestAt = entry.Value;
                        oldest = entry.Key;
                    }
                }

                if (oldest == null || !_lastUsed.Remove(oldest))
                {
                    return;
                }
            }
        }
    }
}
