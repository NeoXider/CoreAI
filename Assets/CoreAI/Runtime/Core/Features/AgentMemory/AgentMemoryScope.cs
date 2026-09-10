using System;
using System.Collections.Generic;
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
                if (string.IsNullOrEmpty(actorId))
                {
                    return Resolve(captured, roleId);
                }

                // WHY: "local" is the anonymous single-player default that every host gets for free
                // (CoreServicesInstaller.DefaultLocalHostIdentityProvider issues it with an EMPTY memory
                // scope). It identifies nobody, so it must never outrank a scope the host declared through
                // IAgentMemoryScopeProvider. It used to: every write made INSIDE a turn landed in the
                // unscoped bare-role file while hydration and reads outside the turn used the learner's
                // scope-v1-* file, so a whole lesson fell out of that learner's history and the shared file
                // silently accumulated every learner's turns. A named actor still wins - see ResolveActorId.
                if (IsDefaultLocalActor(actorId) && IsEmptyScope(captured))
                {
                    return ResolveFromProvider(scopeProvider, roleId);
                }

                // WHY: a named actor takes its scope from the turn as well, and falls back to the host
                // provider when the turn carried none. Otherwise the same class of divergence we fixed
                // for "local" comes back: inside the turn the key was computed without a scope, outside
                // it with the provider's scope, and one and the same memory was read under two keys.
                AgentMemoryScope effectiveScope = IsEmptyScope(captured)
                    ? ScopeFromProvider(scopeProvider, roleId)
                    : captured;

                return ResolveActorId(actorId, effectiveScope, roleId);
            }

            return ResolveFromProvider(scopeProvider, roleId);
        }

        private static AgentMemoryScope ScopeFromProvider(IAgentMemoryScopeProvider scopeProvider, string roleId)
        {
            return (scopeProvider ?? new DefaultAgentMemoryScopeProvider()).GetScope(roleId);
        }

        private static string ResolveFromProvider(IAgentMemoryScopeProvider scopeProvider, string roleId)
        {
            return Resolve(ScopeFromProvider(scopeProvider, roleId), roleId);
        }

        internal static string Resolve(AgentMemoryScope scope, string roleId)
        {
            roleId = NormalizeRoleId(roleId);
            if (IsEmptyScope(scope))
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
            return PrefixedDigest(ScopedKeyPrefix, ScopedKeys, canonical.ToString());
        }

        private static string ResolveActorId(string actorId, AgentMemoryScope scope, string roleId)
        {
            if (IsDefaultLocalActor(actorId))
            {
                // WHY: the default local actor carries no identity of its own, so the key is decided by the
                // scope alone - an empty scope keeps the legacy bare-role file, a declared scope wins.
                // Returning the bare role id unconditionally discarded the host's scope and merged
                // every identity into one file.
                return Resolve(scope, roleId);
            }

            // WHY: ActorContext.MemoryScope is promised to be "the tenant, user, session and topic that
            // memory persistence uses". A named actor used to DISCARD those fields: two lessons (topic)
            // or two sessions of the same actor shared one file, and two tenants holding the same actor
            // id all the more so. An empty scope is still encoded the old way (two parts) so that files
            // of already existing named actors without a scope are not orphaned; a non-empty one appends
            // four more parts. The length-prefixed encoding stays injective: the number of parts is
            // recovered unambiguously.
            StringBuilder canonical = new(160);
            AppendCanonicalPart(canonical, actorId);
            if (!IsEmptyScope(scope))
            {
                AppendCanonicalPart(canonical, scope.TenantId);
                AppendCanonicalPart(canonical, scope.UserId);
                AppendCanonicalPart(canonical, scope.SessionId);
                AppendCanonicalPart(canonical, scope.TopicId);
            }

            AppendCanonicalPart(canonical, roleId);
            return PrefixedDigest(ActorKeyPrefix, ActorKeys, canonical.ToString());
        }

        /// <summary>
        /// Whether the id is the reserved anonymous local default rather than a real, named actor.
        /// A named actor keeps owning its durable key across reconnects, so only this one id defers.
        /// </summary>
        private static bool IsDefaultLocalActor(string actorId)
        {
            return string.Equals(actorId, LocalActorIdentityProvider.DefaultActorId, StringComparison.Ordinal);
        }

        private static bool IsEmptyScope(AgentMemoryScope scope)
        {
            return string.IsNullOrWhiteSpace(scope.TenantId) &&
                   string.IsNullOrWhiteSpace(scope.UserId) &&
                   string.IsNullOrWhiteSpace(scope.SessionId) &&
                   string.IsNullOrWhiteSpace(scope.TopicId);
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

        /// <summary>Upper bound on memoized keys per prefix; a table is cleared when it reaches this.</summary>
        private const int KeyCacheCapacity = 512;

        private static readonly object KeyCacheGate = new();
        private static readonly Dictionary<string, string> ScopedKeys = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> ActorKeys = new(StringComparer.Ordinal);

        /// <summary>
        /// <c>prefix + SHA-256 hex of the canonical tuple</c>, memoized by the tuple text.
        /// <para>
        /// WHY: every memory-store call of a scoped host - each history append, each history read, each
        /// memory load and save, several per turn - derived its storage key by hashing the same handful
        /// of (scope, actor, role) tuples again: a fresh <see cref="SHA256"/> instance, the UTF-8 bytes,
        /// thirty-two formatted strings for the hex and the prefixed concatenation. A process sees only
        /// a few distinct tuples, and the key is a pure function of the text, so remembering it is exact;
        /// the tables are small and hold strings only, so they survive domain reloads harmlessly.
        /// </para>
        /// </summary>
        private static string PrefixedDigest(string prefix, Dictionary<string, string> cache, string canonical)
        {
            lock (KeyCacheGate)
            {
                if (cache.TryGetValue(canonical, out string cached))
                {
                    return cached;
                }
            }

            string key = prefix + ComputeSha256Hex(canonical);
            lock (KeyCacheGate)
            {
                if (cache.Count >= KeyCacheCapacity)
                {
                    cache.Clear();
                }

                cache[canonical] = key;
            }

            return key;
        }

        private static string ComputeSha256Hex(string value)
        {
            using SHA256 sha = SHA256.Create();
            return CoreAI.Audit.AuditHash.ByteArrayToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }
    }

    /// <summary>
    /// Public entry point for touching memory ON BEHALF OF an actor outside an orchestrator turn.
    /// <para>
    /// Inside a turn the queue pushes the actor context itself, and every scoped decorator derives the
    /// key from it. Outside (hydrating history for the UI, dropping context when a connection is lost,
    /// migration) nobody pushes an actor, and the key is derived from <see cref="IAgentMemoryScopeProvider"/>
    /// alone - so a host with named actors read and cleared THE WRONG file, not the one the turn wrote.
    /// Wrapping the code in <c>using (AgentMemoryActorScope.Enter(actorContext))</c> yields the same key
    /// as inside the turn. The context must have been issued by an identity provider
    /// (<see cref="ActorContext.IsTrusted"/>): a hand-built one is rejected, so nobody can "enter"
    /// somebody else's memory by forging the struct.
    /// </para>
    /// </summary>
    public static class AgentMemoryActorScope
    {
        /// <summary>Makes <paramref name="actorContext"/> current for memory until <c>Dispose()</c>.</summary>
        /// <exception cref="InvalidOperationException">The context was not issued by an identity provider.</exception>
        public static IDisposable Enter(ActorContext actorContext)
        {
            return AgentMemoryScopeExecutionContext.Push(actorContext);
        }

        /// <summary>Makes <paramref name="scope"/> current for memory until <c>Dispose()</c> (no actor).</summary>
        public static IDisposable Enter(AgentMemoryScope scope)
        {
            return AgentMemoryScopeExecutionContext.Push(scope);
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
