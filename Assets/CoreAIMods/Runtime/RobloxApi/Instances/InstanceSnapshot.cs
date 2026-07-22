using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Roblox.Instances
{
    /// <summary>Attribute value kinds a snapshot can carry: the primitive subset plus the datatype
    /// subset Roblox attributes support that exists today. Datatype kinds serialize their
    /// components into <see cref="AttributeSnapshot.StringValue"/> as a stable invariant-culture
    /// string so capture→restore→capture stays byte-identical.</summary>
    public enum AttributeValueKind
    {
        String,
        Number,
        Bool,
        Vector3,
        Vector2,
        Color3,
        UDim
    }

    /// <summary>One serialized attribute; exactly one value field is meaningful per kind
    /// (datatype kinds encode their components into <see cref="StringValue"/>).</summary>
    [Serializable]
    public sealed class AttributeSnapshot
    {
        public string Name;
        public AttributeValueKind Kind;
        public string StringValue;
        public double NumberValue;
        public bool BoolValue;
    }

    /// <summary>
    /// Serializable record of one instance — the MVP3 world-file data model (roadmap §2, world
    /// file): stable ids from day one, owner/origin ledger metadata, tags and attributes.
    /// Plain DTO by design; the JSON encoding arrives with RobloxJson (MVP2/MVP3).
    /// </summary>
    [Serializable]
    public sealed class InstanceSnapshot
    {
        /// <summary>Serialized InstanceId.Value — stable across save/load, no remap table.</summary>
        public ulong Id;

        /// <summary>0 for the snapshot root.</summary>
        public ulong ParentId;

        public string ClassName;
        public string Name;
        public bool Archivable;
        public string OwnerModId;
        public string OriginTag;
        public List<string> Tags = new List<string>();
        public List<AttributeSnapshot> Attributes = new List<AttributeSnapshot>();
    }

    /// <summary>A captured subtree in preorder (parents always precede children).</summary>
    [Serializable]
    public sealed class InstanceTreeSnapshot
    {
        public List<InstanceSnapshot> Instances = new List<InstanceSnapshot>();
    }
}
