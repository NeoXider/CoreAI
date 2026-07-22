using System;
using System.Collections.Generic;

namespace CoreAI.RobloxApi.Instances
{
    /// <summary>Attribute value kinds an MVP1 snapshot can carry (primitive subset).</summary>
    public enum AttributeValueKind
    {
        String,
        Number,
        Bool
    }

    /// <summary>One serialized attribute; exactly one value field is meaningful per kind.</summary>
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
