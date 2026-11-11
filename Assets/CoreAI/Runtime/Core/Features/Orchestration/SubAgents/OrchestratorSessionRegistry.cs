using System;
using System.Collections.Generic;
using CoreAI.Authority;
using CoreAI.Logging;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// One <see cref="OrchestratorSession"/> per conversation, the singleton a host registers in its container.
    /// Two conversations never share a plan, a queue, a scope or a budget, even when they talk to the same role.
    /// A host removes a session when its chat closes; nothing is evicted behind its back.
    /// </summary>
    public sealed class OrchestratorSessionRegistry : IDisposable
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, OrchestratorSession> _sessions = new(StringComparer.Ordinal);
        private readonly OrchestratorModeOptions _options;
        private readonly ISubAgentRegistry _subAgents;
        private readonly IAiTaskResultService _subAgentRunner;
        private readonly ILlmAsyncMarshaler _asyncMarshaler;
        private readonly ILog _log;
        private readonly ILeadInstructionsProvider _leadInstructions;
        private bool _disposed;

        /// <summary>Creates the registry; see <see cref="OrchestratorSession"/> for the meaning of each dependency.</summary>
        public OrchestratorSessionRegistry(
            OrchestratorModeOptions options,
            ISubAgentRegistry subAgents,
            IAiTaskResultService subAgentRunner,
            ILlmAsyncMarshaler asyncMarshaler = null,
            ILog log = null,
            ILeadInstructionsProvider leadInstructions = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _subAgents = subAgents ?? throw new ArgumentNullException(nameof(subAgents));
            _subAgentRunner = subAgentRunner ?? throw new ArgumentNullException(nameof(subAgentRunner));
            _asyncMarshaler = asyncMarshaler;
            _log = log;
            _leadInstructions = leadInstructions;
        }

        /// <summary>Mode options shared by every session.</summary>
        public OrchestratorModeOptions Options => _options;

        /// <summary>
        /// The key a conversation is filed under: its own id when it has one, otherwise the actor's connection
        /// session plus the lead role, otherwise the lead role alone. The same fallback chain the conversation
        /// identity plan uses for history keys, so the two agree once conversation ids exist.
        /// </summary>
        public static string ResolveConversationKey(string conversationId, ActorContext? actor, string leadRoleId)
        {
            if (!string.IsNullOrWhiteSpace(conversationId))
            {
                return conversationId.Trim();
            }

            string role = string.IsNullOrWhiteSpace(leadRoleId) ? BuiltInAgentRoleIds.Creator : leadRoleId.Trim();
            if (actor.HasValue && !string.IsNullOrEmpty(actor.Value.ActorId))
            {
                return "actor:" + actor.Value.ActorId + "/" + actor.Value.SessionId + "/" + role;
            }

            return role;
        }

        /// <summary>The session of <paramref name="conversationKey"/>, created on first use.</summary>
        public OrchestratorSession GetOrCreate(string conversationKey)
        {
            if (string.IsNullOrWhiteSpace(conversationKey))
            {
                throw new ArgumentException("A conversation key is required.", nameof(conversationKey));
            }

            string key = conversationKey.Trim();
            lock (_lock)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(OrchestratorSessionRegistry));
                }

                if (!_sessions.TryGetValue(key, out OrchestratorSession session))
                {
                    session = new OrchestratorSession(key, _options, _subAgents, _subAgentRunner, _asyncMarshaler,
                        _log, _leadInstructions);
                    _sessions.Add(key, session);
                }

                return session;
            }
        }

        /// <summary>The session of <paramref name="conversationKey"/> when it exists.</summary>
        public bool TryGet(string conversationKey, out OrchestratorSession session)
        {
            session = null;
            if (string.IsNullOrWhiteSpace(conversationKey))
            {
                return false;
            }

            lock (_lock)
            {
                return _sessions.TryGetValue(conversationKey.Trim(), out session);
            }
        }

        /// <summary>Stops and removes the session of <paramref name="conversationKey"/>.</summary>
        public bool Remove(string conversationKey)
        {
            if (string.IsNullOrWhiteSpace(conversationKey))
            {
                return false;
            }

            OrchestratorSession session;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(conversationKey.Trim(), out session))
                {
                    return false;
                }

                _sessions.Remove(conversationKey.Trim());
            }

            session.Dispose();
            return true;
        }

        /// <summary>Every live session.</summary>
        public IReadOnlyList<OrchestratorSession> Sessions
        {
            get
            {
                lock (_lock)
                {
                    return new List<OrchestratorSession>(_sessions.Values).AsReadOnly();
                }
            }
        }

        /// <summary>Stops and removes every session.</summary>
        public void Dispose()
        {
            List<OrchestratorSession> sessions;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                sessions = new List<OrchestratorSession>(_sessions.Values);
                _sessions.Clear();
            }

            foreach (OrchestratorSession session in sessions)
            {
                session.Dispose();
            }
        }
    }
}
