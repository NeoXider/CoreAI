using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Issues and validates <c>Mcp-Session-Id</c> values. Sessions are intentionally lightweight: the
    /// server is stateless behind the scenes, so a session id is a correlation token, never a security
    /// boundary (that is the bearer token in <see cref="McpRequestGuard"/>). Clients that never send one
    /// still work - session ids are optional on every call except as an echo convenience.
    /// <para>
    /// WHY (bounded): every <c>initialize</c> issues a new id and a client that reconnects in a loop -
    /// which MCP clients do on every editor play/stop cycle - would grow this store without limit.
    /// Issuing prunes expired ids and enforces a hard cap, evicting the oldest first.
    /// </para>
    /// </summary>
    public sealed class McpSessionStore
    {
        /// <summary>How long an issued session id is retained before pruning.</summary>
        public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromHours(1);

        /// <summary>Hard cap on retained session ids, regardless of TTL.</summary>
        public const int DefaultMaxSessions = 64;

        private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
        private readonly TimeSpan _timeToLive;
        private readonly int _maxSessions;
        private readonly Func<DateTimeOffset> _clock;

        /// <param name="timeToLive">Session id lifetime; defaults to <see cref="DefaultTimeToLive"/>.</param>
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
        public int Count => _sessions.Count;

        /// <summary>Creates a fresh session id and records it, pruning expired and excess entries first.</summary>
        public string Issue()
        {
            DateTimeOffset now = _clock();
            Prune(now);

            string id = Guid.NewGuid().ToString("N");
            _sessions[id] = now;
            return id;
        }

        /// <summary>True when <paramref name="sessionId"/> was issued by this store and has not expired.</summary>
        public bool IsKnown(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out DateTimeOffset issuedAt))
            {
                return false;
            }

            if (_clock() - issuedAt <= _timeToLive)
            {
                return true;
            }

            _sessions.TryRemove(sessionId, out _);
            return false;
        }

        private void Prune(DateTimeOffset now)
        {
            foreach (KeyValuePair<string, DateTimeOffset> entry in _sessions)
            {
                if (now - entry.Value > _timeToLive)
                {
                    _sessions.TryRemove(entry.Key, out _);
                }
            }

            // WHY: a reconnect storm can outrun the TTL, so make room for the incoming id by dropping the
            // oldest entries until the store is back under the cap.
            while (_sessions.Count >= _maxSessions)
            {
                string oldest = null;
                DateTimeOffset oldestAt = DateTimeOffset.MaxValue;
                foreach (KeyValuePair<string, DateTimeOffset> entry in _sessions)
                {
                    if (entry.Value < oldestAt)
                    {
                        oldestAt = entry.Value;
                        oldest = entry.Key;
                    }
                }

                if (oldest == null || !_sessions.TryRemove(oldest, out _))
                {
                    return;
                }
            }
        }
    }
}
