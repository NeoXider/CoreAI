using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>
    /// The member names setters report through <see cref="InstanceRegistry.AdvanceRevision"/> and
    /// plans carry to a replica: the property names Roblox uses for <c>GetPropertyChangedSignal</c>,
    /// plus prefixed forms for attributes and tags, which Roblox addresses by name rather than as
    /// properties.
    /// </summary>
    public static class ReplicationMembers
    {
        public const string Name = "Name";

        public const string Archivable = "Archivable";

        /// <summary>The instance moved in the tree.</summary>
        /// <remarks>
        /// WHY carried although Instance.yaml tags <c>Parent</c> NotReplicated: the tag says the
        /// property is not sent as a property, and the same yaml's "Object Replication" section says
        /// what is — an instance reaches clients once it is parented under something replicated, and
        /// a reparent reaches them as a move in the hierarchy. This member is that move, under the
        /// name <c>GetPropertyChangedSignal</c> fires for it. One of the two written deviations
        /// GuardedReplicationFilterEditModeTests holds against the mirror.
        /// </remarks>
        public const string Parent = "Parent";

        /// <summary>
        /// A child was added to or removed from this instance. Structural only: the child's own
        /// <see cref="Parent"/> carries the change, so this member is never planned into a patch.
        /// </summary>
        public const string Children = "Children";

        /// <summary>The payload of a <see cref="RbxValueBase"/>.</summary>
        public const string Value = "Value";

        public const string PrimaryPart = "PrimaryPart";

        /// <summary>
        /// The stored pivot of a <see cref="RbxModel"/> (<see cref="RbxModel.StoredWorldPivot"/>);
        /// its payload is empty while <see cref="RbxModel.HasStoredWorldPivot"/> is false.
        /// </summary>
        /// <remarks>
        /// WHY carried although Model.yaml tags <c>WorldPivot</c> NotReplicated (OURS is the name,
        /// not the behaviour): in Roblox the scriptable <c>WorldPivot</c> is a proxy that cannot be
        /// saved either (serialization can_load/can_save false), and the state behind it is the hidden
        /// <c>Model.WorldPivotData</c> — Full-API-Dump 0.731: OptionalCoordinateFrame, Hidden,
        /// NotScriptable, CanLoad/CanSave true, no NotReplicated tag — which does reach clients, so a
        /// replica's <c>Model:GetPivot()</c> agrees with the server after a <c>PivotTo</c> on a model
        /// without a PrimaryPart. CoreAI keeps that state as the stored pivot, and this member is
        /// WorldPivotData under the scriptable name: the name <c>GetPropertyChangedSignal</c> fires
        /// with, the name the dirty set records and the applier reads. Dropping it would park a
        /// replica's pivot where the model was first built — a divergence Roblox does not have. The
        /// second written deviation GuardedReplicationFilterEditModeTests holds against the mirror.
        /// </remarks>
        public const string WorldPivot = "WorldPivot";

        /// <summary>
        /// One attribute, by name. Instance.yaml lists no property for attributes; Full-API-Dump
        /// 0.731 carries them as <c>Instance.AttributesReplicate</c> (Hidden, NotScriptable, untagged).
        /// </summary>
        public const string AttributePrefix = "Attribute:";

        /// <summary>
        /// One tag, by name. Instance.yaml lists no property for tags; Full-API-Dump 0.731 carries
        /// them as <c>Instance.Tags</c> (Hidden, NotScriptable, untagged), and CollectionService.yaml
        /// says tags replicate from the server to the client.
        /// </summary>
        public const string TagPrefix = "Tag:";

        public static string Attribute(string attribute)
        {
            return AttributePrefix + attribute;
        }

        public static string Tag(string tag)
        {
            return TagPrefix + tag;
        }

        public static bool TryGetAttribute(string member, out string attribute)
        {
            if (member != null && member.Length > AttributePrefix.Length
                && member.StartsWith(AttributePrefix, StringComparison.Ordinal))
            {
                attribute = member.Substring(AttributePrefix.Length);
                return true;
            }

            attribute = null;
            return false;
        }

        public static bool TryGetTag(string member, out string tag)
        {
            if (member != null && member.Length > TagPrefix.Length
                && member.StartsWith(TagPrefix, StringComparison.Ordinal))
            {
                tag = member.Substring(TagPrefix.Length);
                return true;
            }

            tag = null;
            return false;
        }

        /// <summary>True for members that describe tree shape rather than replicable state.</summary>
        public static bool IsStructural(string member)
        {
            return string.Equals(member, Children, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every replicable member the engine-free core knows a live instance to have right now: the
        /// common properties, one entry per attribute and tag, and the class-specific properties. An
        /// attribute or tag the instance lost this step is not here; only the member marks the dirty
        /// set recorded by name can carry it.
        /// </summary>
        /// <remarks>
        /// WHY a whole-node change is expanded into names rather than sent as "everything": a plan
        /// that says everything cannot be filtered member by member, and a reader that has to guess
        /// what everything means will disagree with the applier the first time a class grows a field.
        /// WHY each name here answers to its class yaml's member-level tags: the mirror marks what
        /// does not replicate per member as well as per class. Name, Archivable, PrimaryPart, the
        /// Value of every live value class, and Player's Character and DisplayName are untagged;
        /// <see cref="Parent"/> and <see cref="WorldPivot"/> are tagged NotReplicated and carried
        /// for the reasons written on each. GuardedReplicationFilterEditModeTests holds that
        /// transcription, so a member added here without a verdict fails a test instead of
        /// reaching every client.
        /// WHY a Player's two members are enumerated at all: a spawn carries exactly the members
        /// named here, and the applier hydrates nothing it is not told to — so a member missing
        /// from this list never reaches a replica with the first snapshot, filter or no filter.
        /// </remarks>
        public static List<string> EnumerateReplicable(RbxInstance instance, bool includeParent)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            List<string> members = new() { Name, Archivable };
            if (includeParent)
            {
                members.Add(Parent);
            }

            List<string> attributes = new(instance.GetAttributes().Keys);
            attributes.Sort(StringComparer.Ordinal);
            for (int index = 0; index < attributes.Count; index++)
            {
                members.Add(Attribute(attributes[index]));
            }

            IReadOnlyList<string> tags = instance.GetTags();
            for (int index = 0; index < tags.Count; index++)
            {
                members.Add(Tag(tags[index]));
            }

            if (instance is RbxValueBase)
            {
                members.Add(Value);
            }

            if (instance is RbxPlayer)
            {
                members.Add(RbxPlayer.CharacterMember);
                members.Add(RbxPlayer.DisplayNameMember);
            }

            if (instance is RbxModel)
            {
                members.Add(PrimaryPart);
                members.Add(WorldPivot);
            }

            return members;
        }
    }
}
