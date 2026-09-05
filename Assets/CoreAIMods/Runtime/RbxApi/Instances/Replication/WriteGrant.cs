using System;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>What a grant lets its holder do. Flags, because a grant is rarely all-or-nothing.</summary>
    [Flags]
    public enum WriteGrantActions
    {
        /// <summary>Nothing. A grant with no actions is refused at issue.</summary>
        None = 0,

        /// <summary>Assign a property on an existing instance.</summary>
        WriteProperty = 1,

        /// <summary>Set or clear an attribute.</summary>
        SetAttribute = 1 << 1,

        /// <summary>Add or remove a CollectionService tag.</summary>
        Tag = 1 << 2,

        /// <summary>Move an instance to a different parent.</summary>
        Reparent = 1 << 3,

        /// <summary>Create a new instance in scope.</summary>
        Create = 1 << 4,

        /// <summary>Destroy an instance in scope.</summary>
        Destroy = 1 << 5,

        /// <summary>Everything above — a co-builder's grant.</summary>
        All = WriteProperty | SetAttribute | Tag | Reparent | Create | Destroy
    }

    /// <summary>How far a grant reaches.</summary>
    public enum WriteGrantScope
    {
        /// <summary>One named instance and nothing else.</summary>
        Instance,

        /// <summary>An instance and everything currently under it.</summary>
        Subtree,

        /// <summary>The whole world.</summary>
        World
    }

    /// <summary>
    /// One host-issued permission for a client to change the world.
    /// </summary>
    /// <remarks>
    /// WHY every field is server-owned: the grant is the answer to "may this client write", and any
    /// part of it a client could influence would be a part it could forge. The id is a server GUID,
    /// the grantee is a DURABLE actor id (not a UserId a client states, not a session that changes on
    /// reconnect), and the issuer is recorded so an audit can say who opened the door.
    /// <para>
    /// A grant is immutable except for revocation, which is one-way. Editing a live grant would mean
    /// a permission that changes under an in-flight write; revoking and re-issuing makes both the
    /// old and the new decision visible in the audit.
    /// </para>
    /// </remarks>
    public sealed class WriteGrant
    {
        private bool _revoked;

        /// <summary>Creates a grant. Only the ledger may call this.</summary>
        internal WriteGrant(string grantId, string granteeActorId, WriteGrantScope scope,
            InstanceId scopeRoot, WriteGrantActions actions, string issuedByActorId,
            long issuedAtUnixSeconds, long? expiresAtUnixSeconds)
        {
            GrantId = grantId;
            GranteeActorId = granteeActorId;
            Scope = scope;
            ScopeRoot = scopeRoot;
            Actions = actions;
            IssuedByActorId = issuedByActorId;
            IssuedAtUnixSeconds = issuedAtUnixSeconds;
            ExpiresAtUnixSeconds = expiresAtUnixSeconds;
        }

        /// <summary>Server-generated id; never supplied by a client.</summary>
        public string GrantId { get; }

        /// <summary>The durable actor allowed to write. Not a UserId, not a session id.</summary>
        public string GranteeActorId { get; }

        /// <summary>How far this grant reaches.</summary>
        public WriteGrantScope Scope { get; }

        /// <summary>The instance a non-World grant is anchored to.</summary>
        public InstanceId ScopeRoot { get; }

        /// <summary>What the grantee may do inside the scope.</summary>
        public WriteGrantActions Actions { get; }

        /// <summary>The unrestricted host actor that issued it, for the audit trail.</summary>
        public string IssuedByActorId { get; }

        /// <summary>When it was issued.</summary>
        public long IssuedAtUnixSeconds { get; }

        /// <summary>When it lapses on its own, or null for "until revoked".</summary>
        public long? ExpiresAtUnixSeconds { get; }

        /// <summary>True once revoked; revocation is immediate and one-way.</summary>
        public bool Revoked => _revoked;

        /// <summary>True when the grant is neither revoked nor expired at <paramref name="nowUnixSeconds"/>.</summary>
        public bool IsLiveAt(long nowUnixSeconds)
        {
            return !_revoked
                   && (!ExpiresAtUnixSeconds.HasValue
                       || nowUnixSeconds < ExpiresAtUnixSeconds.Value);
        }

        /// <summary>Revokes the grant. Only the ledger may call this.</summary>
        internal void Revoke()
        {
            _revoked = true;
        }
    }
}
