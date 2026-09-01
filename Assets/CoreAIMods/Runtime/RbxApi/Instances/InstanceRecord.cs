namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Caller-supplied identity and optimistic-concurrency contract for one world mutation.
    /// Actor and operation identities are durable across connection sessions.
    /// </summary>
    public readonly struct MutationEnvelope
    {
        public MutationEnvelope(string actorId, InstanceId targetInstanceId,
            string operationId, long expectedRevision)
        {
            if (string.IsNullOrWhiteSpace(actorId))
            {
                throw new System.ArgumentException(
                    "Mutation actor id must be a non-empty durable identity.", nameof(actorId));
            }

            if (!targetInstanceId.IsValid)
            {
                throw new System.ArgumentException(
                    "Mutation target instance id must be valid.", nameof(targetInstanceId));
            }

            if (string.IsNullOrWhiteSpace(operationId))
            {
                throw new System.ArgumentException(
                    "Mutation operation id must be caller-supplied and non-empty.",
                    nameof(operationId));
            }

            if (expectedRevision < 0L)
            {
                throw new System.ArgumentOutOfRangeException(nameof(expectedRevision),
                    expectedRevision, "Expected revision cannot be negative.");
            }

            ActorId = actorId.Trim();
            TargetInstanceId = targetInstanceId;
            OperationId = operationId.Trim();
            ExpectedRevision = expectedRevision;
        }

        /// <summary>Durable actor identity; connection/session ids never participate.</summary>
        public string ActorId { get; }

        /// <summary>The instance whose current revision gates the operation.</summary>
        public InstanceId TargetInstanceId { get; }

        /// <summary>Caller-generated idempotency key, unique within the durable actor.</summary>
        public string OperationId { get; }

        /// <summary>Revision the caller observed before submitting the operation.</summary>
        public long ExpectedRevision { get; }
    }

    /// <summary>Actor-level mutation and lifecycle policy for one world instance.</summary>
    public enum InstanceAccessScope
    {
        Owned,
        SharedWritable,
        HostProtected
    }

    /// <summary>
    /// The single reconciliation point for the three identity spaces (roadmap §3.3):
    /// Roblox InstanceId, future Mirror netId, and the CoreAI world-command name. Exactly one
    /// record per live instance; lookup by any key returns the same record.
    /// </summary>
    public sealed class InstanceRecord
    {
        /// <summary>Stable in-session id; 0 = invalid; never reused.</summary>
        public InstanceId Id { get; }

        /// <summary>0 until Mirror binds it (MVP12); server-assigned.</summary>
        public uint NetId { get; internal set; }

        /// <summary>CoreAI world-command name; null until bound to a world object.</summary>
        public string WorldName { get; internal set; }

        /// <summary>The live instance this record identifies.</summary>
        public RbxInstance Instance { get; }

        /// <summary>Teardown owner (hot reload); null = host/world-owned.</summary>
        public string OwnerModId { get; }

        /// <summary>Ownership-ledger origin: "mod:&lt;id&gt;" | "console:&lt;invocationId&gt;" |
        /// "ai:&lt;modId&gt;" | null (host). See <see cref="OriginTag"/>.</summary>
        public string OriginTag { get; }

        /// <summary>Durable actor owner; null identifies the host.</summary>
        public string OwnerActorId { get; internal set; }

        /// <summary>Actor-level access policy, independent of teardown ownership and provenance.</summary>
        public InstanceAccessScope AccessScope { get; internal set; }

        /// <summary>True for runtime plumbing that is registered for API identity but is not authored
        /// world content or actor quota usage. Infrastructure may retain an explicit actor owner when
        /// the runtime identity itself requires ACL protection.</summary>
        public bool IsRuntimeInfrastructure { get; }

        /// <summary>True for ledger-attributed user/mod content, excluding host/runtime records.</summary>
        public bool IsAuthoredContent => !IsRuntimeInfrastructure
                                         && (!string.IsNullOrWhiteSpace(OwnerModId)
                                             || !string.IsNullOrWhiteSpace(OriginTag));

        /// <summary>Monotonic in-world revision advanced by successful instance mutations.</summary>
        public long Revision { get; internal set; }

        /// <summary>True while the backing binder holds a materialized backing object (D5).</summary>
        public bool IsMaterialized { get; internal set; }

        internal InstanceRecord(InstanceId id, RbxInstance instance, string ownerModId, string originTag,
            string ownerActorId, InstanceAccessScope accessScope, bool isRuntimeInfrastructure)
        {
            Id = id;
            Instance = instance;
            OwnerModId = ownerModId;
            OriginTag = originTag;
            OwnerActorId = ownerActorId;
            AccessScope = accessScope;
            IsRuntimeInfrastructure = isRuntimeInfrastructure;
        }
    }
}
