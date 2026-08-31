using System;
using System.Collections.Generic;
using CoreAI.Ai;

namespace CoreAI.Authority
{
    /// <summary>
    /// Immutable, Lua-independent set of grants carried by an actor.
    /// </summary>
    public sealed class ActorGrantSet
    {
        private static readonly ActorGrantSet EmptyValue = new(false, new HashSet<string>(StringComparer.Ordinal));
        private static readonly ActorGrantSet UnrestrictedValue = new(true, null);

        private readonly HashSet<string> _grants;
        private readonly bool _isUnrestricted;

        private ActorGrantSet(bool isUnrestricted, HashSet<string> grants)
        {
            _isUnrestricted = isUnrestricted;
            _grants = grants;
        }

        /// <summary>Grant set that permits no capabilities.</summary>
        public static ActorGrantSet None => EmptyValue;

        /// <summary>Root grant set used when legacy local behavior must remain unrestricted.</summary>
        public static ActorGrantSet Unrestricted => UnrestrictedValue;

        /// <summary>Whether this set permits every abstract grant.</summary>
        public bool IsUnrestricted => _isUnrestricted;

        /// <summary>Creates an immutable grant set from abstract grant identifiers.</summary>
        public static ActorGrantSet Create(IEnumerable<string> grants)
        {
            if (grants == null)
            {
                throw new ArgumentNullException(nameof(grants));
            }

            HashSet<string> copy = new(StringComparer.Ordinal);
            foreach (string grant in grants)
            {
                if (string.IsNullOrWhiteSpace(grant))
                {
                    throw new ArgumentException("Grant identifiers must be non-empty strings.", nameof(grants));
                }

                copy.Add(grant.Trim());
            }

            return copy.Count == 0 ? None : new ActorGrantSet(false, copy);
        }

        /// <summary>Returns whether the set contains the supplied abstract grant.</summary>
        public bool Contains(string grant)
        {
            if (string.IsNullOrWhiteSpace(grant))
            {
                return false;
            }

            return _isUnrestricted || _grants.Contains(grant.Trim());
        }

        /// <summary>
        /// Returns the intersection with a restriction. Applying a broader restriction cannot add grants.
        /// </summary>
        public ActorGrantSet NarrowTo(ActorGrantSet restriction)
        {
            if (restriction == null)
            {
                throw new ArgumentNullException(nameof(restriction));
            }

            if (_isUnrestricted)
            {
                return restriction;
            }

            if (restriction._isUnrestricted)
            {
                return this;
            }

            if (_grants.Count == 0 || restriction._grants.Count == 0)
            {
                return None;
            }

            HashSet<string> intersection = new(_grants, StringComparer.Ordinal);
            intersection.IntersectWith(restriction._grants);
            return intersection.Count == 0 ? None : new ActorGrantSet(false, intersection);
        }

        /// <summary>
        /// Returns the intersection with the supplied grant identifiers.
        /// </summary>
        public ActorGrantSet NarrowTo(IEnumerable<string> restriction)
        {
            return NarrowTo(Create(restriction));
        }
    }

    /// <summary>
    /// Immutable identity and authority context for one actor connection.
    /// </summary>
    public readonly struct ActorContext
    {
        private readonly string _actorId;
        private readonly ActorGrantSet _grants;
        private readonly AgentMemoryScope _memoryScope;
        private readonly string _roleId;
        private readonly string _sessionId;
        private readonly bool _trusted;
        private readonly string _worldId;

        private ActorContext(
            string actorId,
            string sessionId,
            string roleId,
            string worldId,
            ActorGrantSet grants,
            AgentMemoryScope memoryScope)
        {
            _actorId = RequireId(actorId, nameof(actorId));
            _sessionId = RequireId(sessionId, nameof(sessionId));
            _roleId = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            _worldId = worldId?.Trim() ?? "";
            _grants = grants ?? throw new ArgumentNullException(nameof(grants));
            _memoryScope = memoryScope;
            _trusted = true;
        }

        /// <summary>Durable identity used for memory, quotas, ownership, and audit.</summary>
        public string ActorId => _actorId ?? "";

        /// <summary>Connection identity used only for cancellation.</summary>
        public string SessionId => _sessionId ?? "";

        /// <summary>Agent role, orthogonal to actor identity.</summary>
        public string RoleId => _roleId ?? "";

        /// <summary>World in which the actor is operating.</summary>
        public string WorldId => _worldId ?? "";

        /// <summary>Immutable abstract grant set.</summary>
        public ActorGrantSet Grants => _grants ?? ActorGrantSet.None;

        /// <summary>
        /// Existing tenant, user, gameplay-session, and topic scope used by memory persistence.
        /// </summary>
        public AgentMemoryScope MemoryScope => _memoryScope;

        /// <summary>Whether this value was issued by an identity provider.</summary>
        public bool IsTrusted => _trusted;

        /// <summary>Creates a copy whose grants are intersected with a narrower set.</summary>
        public ActorContext NarrowGrants(ActorGrantSet restriction)
        {
            AssertTrusted();
            ActorGrantSet narrowed = _grants.NarrowTo(restriction);
            return new ActorContext(ActorId, SessionId, RoleId, WorldId, narrowed, MemoryScope);
        }

        internal static ActorContext Issue(
            string actorId,
            string sessionId,
            string roleId,
            string worldId,
            ActorGrantSet grants,
            AgentMemoryScope memoryScope)
        {
            return new ActorContext(actorId, sessionId, roleId, worldId, grants, memoryScope);
        }

        internal void AssertTrusted()
        {
            if (!_trusted)
            {
                throw new InvalidOperationException("Actor context was not issued by an identity provider.");
            }
        }

        private static string RequireId(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Actor and session identifiers must be non-empty strings.", parameterName);
            }

            return value.Trim();
        }
    }
}
