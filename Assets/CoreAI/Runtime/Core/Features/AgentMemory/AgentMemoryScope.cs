using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CoreAI.Authority;

namespace CoreAI.Ai
{
    /// <summary>
    /// Optional scope values used to isolate agent memory across users, sessions, topics, or tenants.
    /// </summary>
    public readonly struct AgentMemoryScope
    {
        /// <summary>
        /// Creates an immutable memory scope.
        /// </summary>
        public AgentMemoryScope(string tenantId, string userId, string sessionId, string topicId)
        {
            TenantId = tenantId ?? "";
            UserId = userId ?? "";
            SessionId = sessionId ?? "";
            TopicId = topicId ?? "";
        }

        /// <summary>Product or organization boundary.</summary>
        public string TenantId { get; }

        /// <summary>Current player, learner, or account id.</summary>
        public string UserId { get; }

        /// <summary>Current gameplay, chat, lesson, or practice session id.</summary>
        public string SessionId { get; }

        /// <summary>Optional domain topic, quest, scene, or course id.</summary>
        public string TopicId { get; }

        /// <summary>Default empty scope that preserves role-only memory keys.</summary>
        public static AgentMemoryScope Empty => new("", "", "", "");
    }

    /// <summary>
    /// Single canonical role/scope key mapping shared by memory, chat, transcript, and conversation summaries.
    /// Internal so persistence adapters cannot accidentally invent a second encoding.
    /// </summary>
    internal static class AgentMemoryScopeKey
    {
        internal const string ScopedKeyPrefix = "scope-v1-";
        internal const string ActorKeyPrefix = "actor-v1-";

        internal static string Resolve(IAgentMemoryScopeProvider scopeProvider, string roleId)
        {
            roleId = NormalizeRoleId(roleId);
            if (AgentMemoryScopeExecutionContext.TryGet(
                    out AgentMemoryScope captured,
                    out string actorId))
            {
                return string.IsNullOrEmpty(actorId)
                    ? Resolve(captured, roleId)
                    : ResolveActorId(actorId, roleId);
            }

            AgentMemoryScope scope = (scopeProvider ?? new DefaultAgentMemoryScopeProvider()).GetScope(roleId);
            return Resolve(scope, roleId);
        }

        internal static string Resolve(ActorContext actorContext, string roleId)
        {
            actorContext.AssertTrusted();
            return ResolveActorId(actorContext.ActorId, NormalizeRoleId(roleId));
        }

        internal static string Resolve(AgentMemoryScope scope, string roleId)
        {
            roleId = NormalizeRoleId(roleId);
            if (string.IsNullOrWhiteSpace(scope.TenantId) &&
                string.IsNullOrWhiteSpace(scope.UserId) &&
                string.IsNullOrWhiteSpace(scope.SessionId) &&
                string.IsNullOrWhiteSpace(scope.TopicId))
            {
                return roleId;
            }

            // WHY: Scoped ids commonly contain account/learner PII. Persisting the old readable,
            // length-prefixed mapping exposed those ids in filenames and could collide on a
            // case-insensitive filesystem when two identities differed only by case. A full digest
            // is opaque and turns case differences into unrelated lowercase filenames.
            StringBuilder canonical = new(128);
            AppendCanonicalPart(canonical, scope.TenantId);
            AppendCanonicalPart(canonical, scope.UserId);
            AppendCanonicalPart(canonical, scope.SessionId);
            AppendCanonicalPart(canonical, scope.TopicId);
            AppendCanonicalPart(canonical, roleId);
            return ScopedKeyPrefix + Sha256Hex(canonical.ToString());
        }

        private static string ResolveActorId(string actorId, string roleId)
        {
            if (string.Equals(actorId, LocalActorIdentityProvider.DefaultActorId, StringComparison.Ordinal))
            {
                return roleId;
            }

            StringBuilder canonical = new(64);
            AppendCanonicalPart(canonical, actorId);
            AppendCanonicalPart(canonical, roleId);
            return ActorKeyPrefix + Sha256Hex(canonical.ToString());
        }

        private static string NormalizeRoleId(string roleId)
        {
            return string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
        }

        /// <summary>
        /// Length-prefixes every trimmed component before hashing, keeping the tuple encoding injective.
        /// Empty-scope bare-role data is deliberately not folded into this encoding: importing a legacy
        /// role file into a user scope must be an explicit host migration so the first scoped user cannot
        /// accidentally claim data that used to be shared by every user of that role.
        /// </summary>
        private static void AppendCanonicalPart(StringBuilder sb, string value)
        {
            string raw = value?.Trim() ?? "";
            sb.Append(raw.Length).Append(':').Append(raw).Append(';');
        }

        private static string Sha256Hex(string value)
        {
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
            {
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            }

            StringBuilder sb = new(digest.Length * 2);
            for (int i = 0; i < digest.Length; i++)
            {
                sb.Append(digest[i].ToString("x2"));
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Carries an immutable enqueue-time memory scope across asynchronous orchestration execution.
    /// </summary>
    internal static class AgentMemoryScopeExecutionContext
    {
        private static readonly AsyncLocal<Frame> Current = new();

        internal static IDisposable Push(AgentMemoryScope scope)
        {
            Frame previous = Current.Value;
            Frame frame = new(scope, "");
            Current.Value = frame;
            return new Lease(frame, previous);
        }

        internal static IDisposable Push(ActorContext actorContext)
        {
            actorContext.AssertTrusted();
            Frame previous = Current.Value;
            Frame frame = new(actorContext.MemoryScope, actorContext.ActorId);
            Current.Value = frame;
            return new Lease(frame, previous);
        }

        internal static bool TryGet(out AgentMemoryScope scope, out string actorId)
        {
            Frame frame = Current.Value;
            if (frame == null)
            {
                scope = AgentMemoryScope.Empty;
                actorId = "";
                return false;
            }

            scope = frame.Scope;
            actorId = frame.ActorId;
            return true;
        }

        private sealed class Frame
        {
            internal Frame(AgentMemoryScope scope, string actorId)
            {
                Scope = scope;
                ActorId = actorId ?? "";
            }

            internal AgentMemoryScope Scope { get; }
            internal string ActorId { get; }
        }

        private sealed class Lease : IDisposable
        {
            private readonly Frame _owned;
            private readonly Frame _previous;
            private int _disposed;

            internal Lease(Frame owned, Frame previous)
            {
                _owned = owned;
                _previous = previous;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                if (ReferenceEquals(Current.Value, _owned))
                {
                    Current.Value = _previous;
                }
            }
        }
    }
}
