using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>
    /// The BasePart members of the replicated whitelist, under the names Roblox uses for
    /// <c>GetPropertyChangedSignal</c>. The engine-free core does not hold their values: a part layer
    /// reads them on the server (<see cref="IReplicationPartReader"/>) and applies them on a replica
    /// (<see cref="IReplicationPartSink"/>); the wire codec carries them between the two.
    /// </summary>
    /// <remarks>
    /// WHY <see cref="Color"/> is the member name although Full-API-Dump tags <c>BasePart.Color</c>
    /// NotReplicated: the value that does replicate is its hidden storage <c>BasePart.Color3uint8</c>
    /// (Hidden, NotScriptable, CanLoad/CanSave), so the codec sends Color as eight bits per channel
    /// and a replica reads back exactly what a Roblox client reads.
    /// </remarks>
    public static class ReplicationPartMembers
    {
        public const string CFrame = "CFrame";

        public const string Color = "Color";

        public const string Transparency = "Transparency";

        public const string Anchored = "Anchored";

        /// <summary>Maps a member name to its part field; false for every other member.</summary>
        public static bool TryGetField(string member, out ReplicationPartFields field)
        {
            if (string.Equals(member, CFrame, StringComparison.Ordinal))
            {
                field = ReplicationPartFields.CFrame;
                return true;
            }

            if (string.Equals(member, Color, StringComparison.Ordinal))
            {
                field = ReplicationPartFields.Color;
                return true;
            }

            if (string.Equals(member, Transparency, StringComparison.Ordinal))
            {
                field = ReplicationPartFields.Transparency;
                return true;
            }

            if (string.Equals(member, Anchored, StringComparison.Ordinal))
            {
                field = ReplicationPartFields.Anchored;
                return true;
            }

            field = ReplicationPartFields.None;
            return false;
        }
    }

    /// <summary>Which part members a <see cref="ReplicationPartState"/> carries.</summary>
    [Flags]
    public enum ReplicationPartFields : byte
    {
        None = 0,
        CFrame = 1,
        Color = 2,
        Transparency = 4,
        Anchored = 8,
        All = CFrame | Color | Transparency | Anchored
    }

    /// <summary>
    /// The part members of one instance as one value: what a server's part layer reports and what a
    /// replica's part layer receives. A field outside <see cref="Fields"/> carries nothing.
    /// </summary>
    public readonly struct ReplicationPartState
    {
        /// <summary>Creates a state carrying the given fields.</summary>
        public ReplicationPartState(ReplicationPartFields fields, RbxCFrame cframe, RbxColor3 color,
            float transparency, bool anchored)
        {
            Fields = fields & ReplicationPartFields.All;
            CFrame = cframe;
            Color = color;
            Transparency = transparency;
            Anchored = anchored;
        }

        /// <summary>A state carrying every part member.</summary>
        public static ReplicationPartState Of(RbxCFrame cframe, RbxColor3 color, float transparency,
            bool anchored)
        {
            return new ReplicationPartState(ReplicationPartFields.All, cframe, color, transparency, anchored);
        }

        public ReplicationPartFields Fields { get; }

        public RbxCFrame CFrame { get; }

        public RbxColor3 Color { get; }

        public float Transparency { get; }

        public bool Anchored { get; }

        public bool IsEmpty => Fields == ReplicationPartFields.None;

        public bool Has(ReplicationPartFields field)
        {
            return (Fields & field) == field && field != ReplicationPartFields.None;
        }

        /// <summary>The same values, narrowed to the fields named.</summary>
        public ReplicationPartState Only(ReplicationPartFields fields)
        {
            return new ReplicationPartState(Fields & fields, CFrame, Color, Transparency, Anchored);
        }

        public override string ToString()
        {
            return "Part[" + Fields + "]";
        }
    }

    /// <summary>Server side: the current part members of an instance, read when a batch is encoded.</summary>
    public interface IReplicationPartReader
    {
        /// <summary>False when the instance has no part members (not a part, or no part layer).</summary>
        bool TryReadPart(InstanceId id, out ReplicationPartState state);
    }

    /// <summary>
    /// Replica side: applies the part members a batch carried to the replica's instance, after the
    /// engine-free applier applied that batch. Called inside the replica's replication apply scope,
    /// so a setter that reports its member to the registry does not mark the replica diverged.
    /// </summary>
    public interface IReplicationPartSink
    {
        void ApplyPart(RbxInstance instance, in ReplicationPartState state);
    }
}
