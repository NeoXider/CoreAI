using System;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Stable per-session instance identity (roadmap §3.3): ulong, monotonic, never reused;
    /// 0 = invalid. The id space is partitioned by the authority bit (top bit): server-assigned
    /// ids have the bit clear, locally-assigned ids have it set. Only server-space ids may ever
    /// cross the wire or land in world files.
    /// WHY: bit-clear for server-space keeps world-file/serialized ids compact, and the solo/host
    /// process IS the server authority, so the common path allocates in server space.
    /// </summary>
    public readonly struct InstanceId : IEquatable<InstanceId>, IComparable<InstanceId>
    {
        /// <summary>Top bit marks a locally-(client-)assigned id.</summary>
        public const ulong AuthorityBit = 1UL << 63;

        public readonly ulong Value;

        public InstanceId(ulong value)
        {
            Value = value;
        }

        public static InstanceId None => default;

        public bool IsValid => Value != 0UL;

        /// <summary>True when the id was allocated by the server authority (top bit clear).</summary>
        public bool IsServerAssigned => IsValid && (Value & AuthorityBit) == 0UL;

        /// <summary>True when the id was allocated locally without server authority (top bit set).</summary>
        public bool IsLocallyAssigned => IsValid && (Value & AuthorityBit) != 0UL;

        public bool Equals(InstanceId other)
        {
            return Value == other.Value;
        }

        public override bool Equals(object obj)
        {
            return obj is InstanceId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        public int CompareTo(InstanceId other)
        {
            return Value.CompareTo(other.Value);
        }

        public static bool operator ==(InstanceId left, InstanceId right)
        {
            return left.Value == right.Value;
        }

        public static bool operator !=(InstanceId left, InstanceId right)
        {
            return left.Value != right.Value;
        }

        public override string ToString()
        {
            return Value.ToString();
        }
    }

    /// <summary>Which identity space an id is allocated in (§3.3 authority partition).</summary>
    public enum InstanceIdAuthority
    {
        Server,
        Local
    }

    /// <summary>
    /// Guard for the future wire-marshal/spawn paths (MVP11+): locally-assigned ids never cross
    /// the wire. Lives here from MVP1 so the rule is testable before any networking exists.
    /// </summary>
    public static class InstanceIdWireContract
    {
        public static void EnsureWireSafe(InstanceId id)
        {
            if (!id.IsValid)
            {
                throw RbxError.BadArgument("InstanceId.None cannot cross the wire",
                    "marshal only registered instances");
            }

            if (id.IsLocallyAssigned)
            {
                throw new RbxError(RbxErrorCode.NotAuthority,
                    "locally-assigned InstanceId " + id.Value +
                    " cannot cross the wire; only server-assigned ids replicate",
                    "create the instance on the server (or wait for the server-assigned id) before marshalling it");
            }
        }
    }
}
