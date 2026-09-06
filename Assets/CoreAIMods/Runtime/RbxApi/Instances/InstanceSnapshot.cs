using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances
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
    /// Plain DTO by design; the JSON encoding arrives with RbxJson (MVP2/MVP3).
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
        public string OwnerActorId;
        public InstanceAccessScope? AccessScope;
        public long Revision;
        public ModelSnapshot Model;
        public ClickDetectorSnapshot ClickDetector;
        public MaterialVariantSnapshot MaterialVariant;
        public ValueSnapshot Value;
        public HumanoidSnapshot Humanoid;
        public List<string> Tags = new();
        public List<AttributeSnapshot> Attributes = new();
    }

    /// <summary>Durable Model/PVInstance state. PrimaryPartId 0 means nil; the explicit pivot flag
    /// distinguishes an unset pivot from an authored identity CFrame.</summary>
    [Serializable]
    public sealed class ModelSnapshot
    {
        public ulong PrimaryPartId;
        public bool HasStoredWorldPivot;
        public string StoredWorldPivot;
    }

    /// <summary>Durable ClickDetector state; the value uses invariant round-trip formatting.</summary>
    [Serializable]
    public sealed class ClickDetectorSnapshot
    {
        public string MaxActivationDistance;
    }

    /// <summary>Durable MaterialVariant state; StudsPerTile uses invariant round-trip formatting.</summary>
    [Serializable]
    public sealed class MaterialVariantSnapshot
    {
        public string BaseMaterial;
        public int BaseMaterialValue;
        public string ColorMap;
        public string NormalMap;
        public string RoughnessMap;
        public string MetalnessMap;
        public string StudsPerTile;
    }

    /// <summary>
    /// Durable Humanoid state, all numbers using invariant round-trip formatting.
    /// </summary>
    /// <remarks>
    /// WHY only these seven fields: Health, MaxHealth, WalkSpeed, JumpPower, JumpHeight,
    /// UseJumpPower and DisplayName are the durable Humanoid state. The MoveTo target, the
    /// state machine and signal subscriptions are transient runtime state that a host recreates
    /// through AttachHost/Advance once it reattaches a character motor, so they are deliberately
    /// NOT persisted.
    /// </remarks>
    [Serializable]
    public sealed class HumanoidSnapshot
    {
        public string Health;
        public string MaxHealth;
        public string WalkSpeed;
        public string JumpPower;
        public string JumpHeight;
        public bool UseJumpPower;
        public string DisplayName;
    }

    /// <summary>Durable ValueBase payload. Scalars and datatypes encode into
    /// <see cref="StringValue"/> with invariant round-trip formatting (longs plain, doubles
    /// "R", Vector3/Color3 comma-joined floats, CFrame 12 comma-joined floats);
    /// <see cref="ObjectTargetId"/> carries an ObjectValue reference (0 means nil).</summary>
    [Serializable]
    public sealed class ValueSnapshot
    {
        public string StringValue;
        public ulong ObjectTargetId;
    }

    /// <summary>A captured subtree in preorder (parents always precede children).</summary>
    [Serializable]
    public sealed class InstanceTreeSnapshot
    {
        public int? WorldAclVersion;
        public List<InstanceSnapshot> Instances = new();
    }
}
