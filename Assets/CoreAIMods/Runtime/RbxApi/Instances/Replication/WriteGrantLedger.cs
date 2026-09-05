using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>
    /// The server's record of which clients the host has let write, and to what.
    /// </summary>
    /// <remarks>
    /// WHY a ledger and not a flag on the actor: "may write" is not a property of a person, it is a
    /// permission over a target that someone granted at a time and can take back. A boolean on the
    /// actor could not answer "which parts", could not expire, and would leave nothing to audit when
    /// a griefed world has to be explained afterwards.
    /// <para>
    /// <b>The host is not in this ledger.</b> The host holds every right because its writes never
    /// enter the client path at all — they originate in the server process under the
    /// composition-issued unrestricted context. There is no "host" row to forge and no <c>IsHost</c>
    /// field on the wire, which is what makes the absence of a special case the security property.
    /// </para>
    /// <para>
    /// Only an unrestricted actor may issue or revoke. That context exists solely inside the server
    /// process, so no remote message, no Lua call and no intent can reach these methods — the
    /// gateway has no grant action at all.
    /// </para>
    /// <para>
    /// WHY the issuer arrives as an id plus a flag rather than as an <c>ActorContext</c>: this
    /// assembly is engine-free AND authority-free by design, exactly like
    /// <see cref="WorldAclAuthorizer"/> next to it. The binding layer holds the real context and
    /// passes what it says; putting the context type here would drag the whole authority stack into
    /// the layer that has to stay portable.
    /// </para>
    /// </remarks>
    public sealed class WriteGrantLedger
    {
        private readonly Dictionary<string, List<WriteGrant>> _byGrantee =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, WriteGrant> _byId = new(StringComparer.Ordinal);
        private readonly InstanceRegistry _registry;
        private readonly Func<long> _nowUnixSeconds;
        private readonly Action<string> _audit;

        /// <summary>Creates a ledger over one world.</summary>
        public WriteGrantLedger(InstanceRegistry registry, Func<long> nowUnixSeconds = null,
            Action<string> audit = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _nowUnixSeconds = nowUnixSeconds
                              ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _audit = audit;
        }

        /// <summary>Every grant ever issued here, revoked ones included, for the audit view.</summary>
        public IReadOnlyCollection<WriteGrant> AllGrants => _byId.Values;

        /// <summary>
        /// Issues a grant. Only an unrestricted host actor may call this.
        /// </summary>
        /// <exception cref="RbxError">
        /// The issuer is untrusted or restricted, the grantee is empty, the action set is empty, or a
        /// scoped grant names an instance the world does not have.
        /// </exception>
        public WriteGrant Issue(string issuerActorId, bool issuerIsUnrestricted,
            string granteeActorId, WriteGrantScope scope,
            InstanceId scopeRoot, WriteGrantActions actions, long? expiresAtUnixSeconds = null)
        {
            RequireHost(issuerActorId, issuerIsUnrestricted, "issue a world write grant");

            if (string.IsNullOrWhiteSpace(granteeActorId))
            {
                throw RbxError.BadArgument(
                    "a write grant needs a grantee actor id",
                    "pass the durable actor id the admission issued for that client");
            }

            if (actions == WriteGrantActions.None)
            {
                throw RbxError.BadArgument(
                    "a write grant with no actions permits nothing",
                    "name at least one action, or do not issue a grant at all");
            }

            if (scope != WriteGrantScope.World && !_registry.TryGet(scopeRoot, out _))
            {
                // A grant anchored to nothing would silently permit nothing — worse than refusing,
                // because the host would believe it had given access.
                throw RbxError.BadArgument(
                    "write grant scope root " + scopeRoot.Value + " is not in this world",
                    "grant World scope, or name an instance that exists");
            }

            long now = _nowUnixSeconds();
            if (expiresAtUnixSeconds.HasValue && expiresAtUnixSeconds.Value <= now)
            {
                throw RbxError.BadArgument(
                    "a write grant cannot expire in the past",
                    "pass a future expiry, or null for 'until revoked'");
            }

            WriteGrant grant = new(
                Guid.NewGuid().ToString("N"), granteeActorId, scope, scopeRoot, actions,
                issuerActorId, now, expiresAtUnixSeconds);

            if (!_byGrantee.TryGetValue(granteeActorId, out List<WriteGrant> grants))
            {
                grants = new List<WriteGrant>();
                _byGrantee.Add(granteeActorId, grants);
            }

            grants.Add(grant);
            _byId.Add(grant.GrantId, grant);
            _audit?.Invoke("[write-grant] issued " + grant.GrantId + " to " + granteeActorId
                           + " scope=" + scope + " root=" + scopeRoot.Value
                           + " actions=" + actions + " by=" + issuerActorId);
            return grant;
        }

        /// <summary>Revokes a grant immediately. Only an unrestricted host actor may call this.</summary>
        public bool Revoke(string issuerActorId, bool issuerIsUnrestricted, string grantId)
        {
            RequireHost(issuerActorId, issuerIsUnrestricted, "revoke a world write grant");
            if (string.IsNullOrEmpty(grantId) || !_byId.TryGetValue(grantId, out WriteGrant grant))
            {
                return false;
            }

            if (grant.Revoked)
            {
                return false;
            }

            grant.Revoke();
            _audit?.Invoke("[write-grant] revoked " + grantId + " (grantee "
                           + grant.GranteeActorId + ") by " + issuerActorId);
            return true;
        }

        /// <summary>Revokes every live grant held by one actor, e.g. when it disconnects.</summary>
        public int RevokeAllFor(string issuerActorId, bool issuerIsUnrestricted,
            string granteeActorId)
        {
            RequireHost(issuerActorId, issuerIsUnrestricted, "revoke a world write grant");
            if (string.IsNullOrEmpty(granteeActorId)
                || !_byGrantee.TryGetValue(granteeActorId, out List<WriteGrant> grants))
            {
                return 0;
            }

            int revoked = 0;
            for (int index = 0; index < grants.Count; index++)
            {
                if (!grants[index].Revoked)
                {
                    grants[index].Revoke();
                    revoked++;
                }
            }

            if (revoked > 0)
            {
                _audit?.Invoke("[write-grant] revoked " + revoked + " grant(s) for "
                               + granteeActorId + " by " + issuerActorId);
            }

            return revoked;
        }

        /// <summary>
        /// Whether <paramref name="actorId"/> may perform <paramref name="action"/> on
        /// <paramref name="target"/> right now.
        /// </summary>
        /// <remarks>
        /// WHY the scope is resolved against the LIVE tree: a subtree grant follows reparenting, so
        /// a part moved out of the granted area stops being writable the moment it moves. Resolving
        /// against a snapshot taken at issue time would leave a client writing to something the host
        /// has since taken away.
        /// </remarks>
        public bool Allows(string actorId, InstanceId target, WriteGrantActions action)
        {
            if (string.IsNullOrEmpty(actorId) || action == WriteGrantActions.None
                || !_byGrantee.TryGetValue(actorId, out List<WriteGrant> grants))
            {
                return false;
            }

            long now = _nowUnixSeconds();
            for (int index = 0; index < grants.Count; index++)
            {
                WriteGrant grant = grants[index];
                if (grant.IsLiveAt(now) && (grant.Actions & action) == action
                    && CoversTarget(grant, target))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The live grants held by one actor, for a Hub view or an audit.</summary>
        public IReadOnlyList<WriteGrant> LiveGrantsFor(string actorId)
        {
            List<WriteGrant> live = new();
            if (string.IsNullOrEmpty(actorId)
                || !_byGrantee.TryGetValue(actorId, out List<WriteGrant> grants))
            {
                return live;
            }

            long now = _nowUnixSeconds();
            for (int index = 0; index < grants.Count; index++)
            {
                if (grants[index].IsLiveAt(now))
                {
                    live.Add(grants[index]);
                }
            }

            return live;
        }

        private bool CoversTarget(WriteGrant grant, InstanceId target)
        {
            if (grant.Scope == WriteGrantScope.World)
            {
                return true;
            }

            if (grant.ScopeRoot.Value == target.Value)
            {
                return true;
            }

            if (grant.Scope != WriteGrantScope.Subtree)
            {
                return false;
            }

            return _registry.TryGet(target, out RbxInstance instance)
                   && _registry.TryGet(grant.ScopeRoot, out RbxInstance root)
                   && instance.IsDescendantOf(root);
        }

        private static void RequireHost(string issuerActorId, bool issuerIsUnrestricted, string what)
        {
            if (string.IsNullOrWhiteSpace(issuerActorId) || !issuerIsUnrestricted)
            {
                throw new RbxError(
                    RbxErrorCode.NotAuthority,
                    "only the host may " + what,
                    "issue grants from the server process; there is no remote, Lua or intent path "
                    + "to this ledger by design");
            }
        }
    }
}
