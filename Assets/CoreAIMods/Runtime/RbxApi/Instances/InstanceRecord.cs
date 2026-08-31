namespace CoreAI.Mods.Rbx.Instances
{
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

        /// <summary>True while the backing binder holds a materialized backing object (D5).</summary>
        public bool IsMaterialized { get; internal set; }

        internal InstanceRecord(InstanceId id, RbxInstance instance, string ownerModId, string originTag,
            string ownerActorId, InstanceAccessScope accessScope)
        {
            Id = id;
            Instance = instance;
            OwnerModId = ownerModId;
            OriginTag = originTag;
            OwnerActorId = ownerActorId;
            AccessScope = accessScope;
        }
    }
}
