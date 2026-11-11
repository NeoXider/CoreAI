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

        /// <summary>
        /// True for a node the runtime rebuilds itself (a world's Terrain, a mod's legacy script folder), which is
        /// neither authored content nor charged to an instance quota. Only an in-memory snapshot carries the flag:
        /// a world package's projection leaves such nodes out, and <c>world.json</c> has no key for it.
        /// </summary>
        public bool IsRuntimeInfrastructure;

        public long Revision;
        public ModelSnapshot Model;
        public ClickDetectorSnapshot ClickDetector;
        public MaterialVariantSnapshot MaterialVariant;
        public ValueSnapshot Value;
        public HumanoidSnapshot Humanoid;
        public PlayerSnapshot Player;

        /// <summary>Script state of a <c>Script</c>, <c>LocalScript</c> or <c>ModuleScript</c>; null on every other class.</summary>
        public ScriptSnapshot Script;

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

    /// <summary>
    /// The identity a Player carries with its node, read off the mirror: <c>Player.UserId</c> and
    /// <c>Player.DisplayName</c> replicate (Player.yaml tags neither NotReplicated),
    /// <c>Player.Character</c> is an instance reference (0 means nil), and the username is the
    /// node's own <see cref="InstanceSnapshot.Name"/>.
    /// </summary>
    /// <remarks>
    /// WHY <see cref="ActorId"/> travels although the mirror has no such property (OURS): the
    /// mirror's <c>Players.LocalPlayer</c> is NotReplicated, so a client must tell its own Player
    /// from the others by the identity its connection carries, and that is the actor id. It is
    /// already on every node as the owner actor; naming it here makes the Player whole on its own.
    /// WHY AccountAge, FollowUserId and Team are absent: Player.yaml tags each NotReplicated.
    /// </remarks>
    [Serializable]
    public sealed class PlayerSnapshot
    {
        public string ActorId;
        public long UserId;
        public string DisplayName;
        public ulong CharacterId;
    }

    /// <summary>
    /// Durable state of a script instance (MVP4 design note, SC-1, SC-3 and SC-4): the store entry it is bound to and,
    /// on a <c>BaseScript</c> (<c>Script</c>, <c>LocalScript</c>), <c>Enabled</c> and the <c>Enum.RunContext</c>
    /// value. A <c>ModuleScript</c> has neither, so both are null on its snapshot. The instance never holds source
    /// text: the source stays in the mod store entry <see cref="SourceId"/> names.
    /// </summary>
    /// <remarks>
    /// A script node without this block restores with the class defaults (unbound, enabled, <c>Legacy</c>), so a
    /// snapshot taken before the block existed and a replication spawn, whose wire never carries it, stay valid.
    /// </remarks>
    [Serializable]
    public sealed class ScriptSnapshot
    {
        /// <summary>The mod id of the bound store entry; empty for an unbound script, never null.</summary>
        public string SourceId;

        /// <summary><c>BaseScript.Enabled</c>; null on a <c>ModuleScript</c>.</summary>
        public bool? Enabled;

        /// <summary><c>BaseScript.RunContext</c> as its <c>Enum.RunContext</c> value; null on a <c>ModuleScript</c>.</summary>
        public int? RunContext;
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
