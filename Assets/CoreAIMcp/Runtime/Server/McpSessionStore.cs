using System;
using System.Collections.Concurrent;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Issues and validates <c>Mcp-Session-Id</c> values. Sessions are intentionally lightweight: the
    /// server is stateless behind the scenes, so a session id is a correlation token, never a security
    /// boundary (the transport is localhost-only, opt-in, no auth). Clients that never send one still
    /// work — session ids are optional on every call except as an echo convenience.
    /// </summary>
    public sealed class McpSessionStore
    {
        private readonly ConcurrentDictionary<string, byte> _sessions = new(StringComparer.Ordinal);

        /// <summary>Creates a fresh session id and records it.</summary>
        public string Issue()
        {
            string id = Guid.NewGuid().ToString("N");
            _sessions[id] = 1;
            return id;
        }

        /// <summary>True when <paramref name="sessionId"/> was issued by this store.</summary>
        public bool IsKnown(string sessionId)
        {
            return !string.IsNullOrEmpty(sessionId) && _sessions.ContainsKey(sessionId);
        }

        /// <summary>Number of live sessions (diagnostics/tests).</summary>
        public int Count => _sessions.Count;
    }
}
