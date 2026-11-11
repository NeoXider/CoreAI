using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>
    /// Where the encoder reads the values a plan names, at the moment it encodes: the engine-free
    /// node state and the part members. A plan carries names; this carries values.
    /// </summary>
    public interface IReplicationWireSource
    {
        /// <summary>The instance's engine-free state now, or null when it is no longer live.</summary>
        InstanceSnapshot CaptureNode(InstanceId id);

        /// <summary>The instance's part members now; false when it has none.</summary>
        bool TryReadPart(InstanceId id, out ReplicationPartState state);
    }

    /// <summary>The server's <see cref="IReplicationWireSource"/>: its registry plus an optional part layer.</summary>
    public sealed class RegistryReplicationWireSource : IReplicationWireSource
    {
        private readonly InstanceRegistry _registry;
        private readonly IReplicationPartReader _parts;

        public RegistryReplicationWireSource(InstanceRegistry registry, IReplicationPartReader parts = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _parts = parts;
        }

        public InstanceSnapshot CaptureNode(InstanceId id)
        {
            return _registry.TryGet(id, out RbxInstance instance) && !instance.IsDestroyed
                ? InstanceTreeSerializer.CaptureNode(instance)
                : null;
        }

        public bool TryReadPart(InstanceId id, out ReplicationPartState state)
        {
            if (_parts != null)
            {
                return _parts.TryReadPart(id, out state);
            }

            state = default;
            return false;
        }
    }

    /// <summary>
    /// Turns one recipient's <see cref="ReplicationBatchPlan"/> into one binary packet (format
    /// version <see cref="ReplicationWireFormat.Version"/>), reusing one buffer: the bytes returned
    /// stay valid until the next <see cref="Encode"/>.
    /// </summary>
    /// <remarks>
    /// WHY the owner, origin and access fields of a captured node are never written: authorization
    /// is decided on the server against the server's records, and a replica that received them would
    /// only publish which durable actor owns what, other players included. Leaving them out at the
    /// encoder is the wire half of stripping them at capture.
    /// <para>
    /// WHY a patch that names only part members never captures the node: capture allocates the
    /// whole engine-free description, and a moving part is the one record sent 20 times a second.
    /// Such a patch costs the encoder no allocation once its buffer has grown.
    /// </para>
    /// </remarks>
    public sealed class ReplicationWireEncoder
    {
        private readonly ReplicationWireWriter _writer;
        private readonly float[] _floats = new float[12];

        public ReplicationWireEncoder(int maxPacketBytes = ReplicationWireFormat.DefaultMaxPacketBytes)
        {
            _writer = new ReplicationWireWriter(maxPacketBytes);
            MaxPacketBytes = maxPacketBytes;
        }

        public int MaxPacketBytes { get; }

        /// <summary>
        /// Encodes the batch with values read from <paramref name="source"/> now; a world batch
        /// (<see cref="ReplicationStream.PlanWorld"/>) is flagged so the replica arms
        /// <see cref="ReplicationApplier.BeginResync"/> for it. Throws <see cref="ReplicationWireException"/>
        /// when the batch cannot travel as planned: a local id, a spawn whose instance is gone, or
        /// more bytes than <see cref="MaxPacketBytes"/>.
        /// </summary>
        public ArraySegment<byte> Encode(ReplicationBatchPlan plan, IReplicationWireSource source,
            bool isWorld = false)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (plan.Sequence < 1L)
            {
                throw new ReplicationWireException(ReplicationWireError.InvalidSequence,
                    "a replication batch sequence starts at 1, not " + plan.Sequence);
            }

            ReplicationWireWriter writer = _writer;
            writer.Reset();
            writer.WriteByte(ReplicationWireFormat.Magic);
            writer.WriteByte(ReplicationWireFormat.Version);
            writer.WriteByte(isWorld ? ReplicationWireFormat.HeaderFlagWorld : (byte)0);
            writer.WriteVarUInt((ulong)plan.Sequence);
            IReadOnlyList<ReplicationOperation> operations = plan.Operations;
            writer.WriteVarUInt((ulong)operations.Count);

            long previousId = 0L;
            long previousRevision = 0L;
            for (int index = 0; index < operations.Count; index++)
            {
                ReplicationOperation operation = operations[index];
                long id = WireId(operation.InstanceId);
                switch (operation.Kind)
                {
                    case ReplicationOperationKind.Remove:
                        writer.WriteByte((byte)ReplicationWireRecordKind.Remove);
                        writer.WriteVarInt(id - previousId);
                        break;
                    case ReplicationOperationKind.Spawn:
                    case ReplicationOperationKind.Patch:
                        WriteStateRecord(operation, id - previousId, previousRevision, source);
                        previousRevision = operation.Revision;
                        break;
                    default:
                        throw new ReplicationWireException(ReplicationWireError.InvalidPlan,
                            "operation " + index + " has unknown kind " + operation.Kind);
                }

                previousId = id;
            }

            return writer.Written;
        }

        /// <summary>The same packet as <see cref="Encode"/>, copied into an array of its own.</summary>
        public byte[] EncodeToArray(ReplicationBatchPlan plan, IReplicationWireSource source,
            bool isWorld = false)
        {
            ArraySegment<byte> written = Encode(plan, source, isWorld);
            byte[] copy = new byte[written.Count];
            Buffer.BlockCopy(written.Array, written.Offset, copy, 0, written.Count);
            return copy;
        }

        private void WriteStateRecord(ReplicationOperation operation, long idDelta, long previousRevision,
            IReplicationWireSource source)
        {
            bool spawn = operation.Kind == ReplicationOperationKind.Spawn;
            InstanceId id = operation.InstanceId;
            if (operation.Revision < 0L)
            {
                throw new ReplicationWireException(ReplicationWireError.InvalidRevision,
                    "instance " + id.Value + " carries negative revision " + operation.Revision);
            }

            IReadOnlyList<string> members = operation.Members;
            ReplicationPartFields namedParts = ReplicationPartFields.None;
            int engineFreeMembers = 0;
            for (int index = 0; index < members.Count; index++)
            {
                if (ReplicationPartMembers.TryGetField(members[index], out ReplicationPartFields field))
                {
                    namedParts |= field;
                }
                else
                {
                    engineFreeMembers++;
                }
            }

            InstanceSnapshot node = null;
            if (spawn || engineFreeMembers > 0)
            {
                node = source.CaptureNode(id);
                if (node == null && spawn)
                {
                    throw new ReplicationWireException(ReplicationWireError.MissingState,
                        "spawn for instance " + id.Value + " has no live state to send");
                }

                // WHY a vanished patch target sends only its revision: the instance was destroyed
                // after the plan was made, so its removal is already in the dirty set for the next
                // step, and there is no value left to send for the members the plan named.
                if (node == null)
                {
                    engineFreeMembers = 0;
                }
            }

            ReplicationPartState part = default;
            bool hasPart = false;
            if (spawn)
            {
                hasPart = source.TryReadPart(id, out part) && !part.IsEmpty;
            }
            else if (namedParts != ReplicationPartFields.None && source.TryReadPart(id, out part))
            {
                part = part.Only(namedParts);
                hasPart = !part.IsEmpty;
            }

            int stateEntries = spawn ? CountSpawnState(node) : 0;
            int entryCount = engineFreeMembers + stateEntries;
            byte head = (byte)((byte)(spawn ? ReplicationWireRecordKind.Spawn : ReplicationWireRecordKind.Patch)
                               | (hasPart ? ReplicationWireFormat.RecordFlagPartBlock : 0)
                               | (entryCount > 0 ? ReplicationWireFormat.RecordFlagMemberList : 0));
            ReplicationWireWriter writer = _writer;
            writer.WriteByte(head);
            writer.WriteVarInt(idDelta);
            writer.WriteVarInt(operation.Revision - previousRevision);
            if (spawn)
            {
                writer.WriteVarUInt((ulong)WireId(new InstanceId(node.ParentId), allowNil: true));
                if (string.IsNullOrEmpty(node.ClassName))
                {
                    throw new ReplicationWireException(ReplicationWireError.MissingState,
                        "spawn for instance " + id.Value + " has no class name");
                }

                writer.WriteString(node.ClassName);
            }

            if (hasPart)
            {
                WritePart(part);
            }

            if (entryCount == 0)
            {
                return;
            }

            writer.WriteVarUInt((ulong)entryCount);
            if (spawn)
            {
                WriteSpawnState(node);
            }

            if (engineFreeMembers == 0)
            {
                return;
            }

            for (int index = 0; index < members.Count; index++)
            {
                string member = members[index];
                if (!ReplicationPartMembers.TryGetField(member, out _))
                {
                    WriteMember(member, node);
                }
            }
        }

        private void WritePart(in ReplicationPartState part)
        {
            ReplicationWireWriter writer = _writer;
            uint mask = 0;
            ReplicationCFrameQuantizer.RotationMode mode = ReplicationCFrameQuantizer.RotationMode.Identity;
            byte axisCode = 0;
            RbxCFrame cframe = part.CFrame;
            if (part.Has(ReplicationPartFields.CFrame))
            {
                mask |= ReplicationWireFormat.PartCFrame;
                mode = ReplicationCFrameQuantizer.Classify(cframe, out axisCode);
                switch (mode)
                {
                    case ReplicationCFrameQuantizer.RotationMode.Quantized:
                        mask |= ReplicationWireFormat.PartRotationQuantized;
                        break;
                    case ReplicationCFrameQuantizer.RotationMode.Raw:
                        mask |= ReplicationWireFormat.PartRotationRaw;
                        break;
                    case ReplicationCFrameQuantizer.RotationMode.AxisAligned:
                        mask |= ReplicationWireFormat.PartRotationQuantized | ReplicationWireFormat.PartRotationRaw;
                        break;
                }
            }

            if (part.Has(ReplicationPartFields.Color))
            {
                mask |= ReplicationWireFormat.PartColor;
            }

            if (part.Has(ReplicationPartFields.Transparency))
            {
                mask |= ReplicationWireFormat.PartTransparency;
            }

            if (part.Has(ReplicationPartFields.Anchored))
            {
                mask |= ReplicationWireFormat.PartAnchored
                        | (part.Anchored ? ReplicationWireFormat.PartAnchoredValue : 0u);
            }

            writer.WriteVarUInt(mask);
            if ((mask & ReplicationWireFormat.PartCFrame) != 0)
            {
                if (mode == ReplicationCFrameQuantizer.RotationMode.Raw)
                {
                    WriteRawCFrame(cframe);
                }
                else
                {
                    WritePosition(cframe.X);
                    WritePosition(cframe.Y);
                    WritePosition(cframe.Z);
                    if (mode == ReplicationCFrameQuantizer.RotationMode.Quantized)
                    {
                        writer.WriteUInt48(ReplicationCFrameQuantizer.PackRotation(cframe));
                    }
                    else if (mode == ReplicationCFrameQuantizer.RotationMode.AxisAligned)
                    {
                        writer.WriteByte(axisCode);
                    }
                }
            }

            if ((mask & ReplicationWireFormat.PartColor) != 0)
            {
                RbxColor3 color = part.Color;
                writer.WriteByte(ReplicationCFrameQuantizer.QuantizeColorChannel(color.R));
                writer.WriteByte(ReplicationCFrameQuantizer.QuantizeColorChannel(color.G));
                writer.WriteByte(ReplicationCFrameQuantizer.QuantizeColorChannel(color.B));
            }

            if ((mask & ReplicationWireFormat.PartTransparency) != 0)
            {
                writer.WriteSingle(part.Transparency);
            }
        }

        private void WritePosition(float coordinate)
        {
            ReplicationCFrameQuantizer.TryQuantizePosition(coordinate, out long fixedValue);
            _writer.WriteVarInt(fixedValue);
        }

        private void WriteRawCFrame(in RbxCFrame cframe)
        {
            RbxVector3 right = cframe.XVector;
            RbxVector3 up = cframe.YVector;
            RbxVector3 back = cframe.ZVector;
            ReplicationWireWriter writer = _writer;
            writer.WriteSingle(cframe.X);
            writer.WriteSingle(cframe.Y);
            writer.WriteSingle(cframe.Z);
            writer.WriteSingle(right.X);
            writer.WriteSingle(up.X);
            writer.WriteSingle(back.X);
            writer.WriteSingle(right.Y);
            writer.WriteSingle(up.Y);
            writer.WriteSingle(back.Y);
            writer.WriteSingle(right.Z);
            writer.WriteSingle(up.Z);
            writer.WriteSingle(back.Z);
        }

        private static int CountSpawnState(InstanceSnapshot node)
        {
            int count = 0;
            if (node.Player != null && !string.IsNullOrWhiteSpace(node.Player.ActorId))
            {
                count++;
            }

            if (node.ClickDetector != null)
            {
                count++;
            }

            if (node.MaterialVariant != null)
            {
                count++;
            }

            if (node.Humanoid != null)
            {
                count++;
            }

            return count;
        }

        /// <summary>
        /// The state a spawn needs besides its members: a Player's identity and the specialized
        /// states the applier restores with a spawn only (ClickDetector, MaterialVariant, Humanoid).
        /// </summary>
        private void WriteSpawnState(InstanceSnapshot node)
        {
            ReplicationWireWriter writer = _writer;
            if (node.Player != null && !string.IsNullOrWhiteSpace(node.Player.ActorId))
            {
                writer.WriteByte(ReplicationWireFormat.StatePlayerIdentity);
                writer.WriteString(node.Player.ActorId);
                writer.WriteVarInt(node.Player.UserId);
            }

            if (node.ClickDetector != null)
            {
                writer.WriteByte(ReplicationWireFormat.StateClickDetector);
                writer.WriteString(node.ClickDetector.MaxActivationDistance);
            }

            if (node.MaterialVariant != null)
            {
                MaterialVariantSnapshot variant = node.MaterialVariant;
                writer.WriteByte(ReplicationWireFormat.StateMaterialVariant);
                writer.WriteString(variant.BaseMaterial);
                writer.WriteVarInt(variant.BaseMaterialValue);
                writer.WriteString(variant.ColorMap);
                writer.WriteString(variant.NormalMap);
                writer.WriteString(variant.RoughnessMap);
                writer.WriteString(variant.MetalnessMap);
                writer.WriteString(variant.StudsPerTile);
            }

            if (node.Humanoid != null)
            {
                HumanoidSnapshot humanoid = node.Humanoid;
                writer.WriteByte(ReplicationWireFormat.StateHumanoid);
                writer.WriteString(humanoid.Health);
                writer.WriteString(humanoid.MaxHealth);
                writer.WriteString(humanoid.WalkSpeed);
                writer.WriteString(humanoid.JumpPower);
                writer.WriteString(humanoid.JumpHeight);
                writer.WriteByte(humanoid.UseJumpPower ? (byte)1 : (byte)0);
                writer.WriteString(humanoid.DisplayName);
            }
        }

        private void WriteMember(string member, InstanceSnapshot node)
        {
            ReplicationWireWriter writer = _writer;
            if (string.Equals(member, ReplicationMembers.Name, StringComparison.Ordinal))
            {
                writer.WriteByte(ReplicationWireFormat.MemberName);
                writer.WriteString(node.Name);
            }
            else if (string.Equals(member, ReplicationMembers.Archivable, StringComparison.Ordinal))
            {
                writer.WriteByte(ReplicationWireFormat.MemberArchivable);
                writer.WriteByte(node.Archivable ? (byte)1 : (byte)0);
            }
            else if (string.Equals(member, ReplicationMembers.Parent, StringComparison.Ordinal))
            {
                writer.WriteByte(ReplicationWireFormat.MemberParent);
                writer.WriteVarUInt((ulong)WireId(new InstanceId(node.ParentId), allowNil: true));
            }
            else if (member.StartsWith(ReplicationMembers.AttributePrefix, StringComparison.Ordinal)
                     && member.Length > ReplicationMembers.AttributePrefix.Length)
            {
                WriteAttribute(member, node);
            }
            else if (member.StartsWith(ReplicationMembers.TagPrefix, StringComparison.Ordinal)
                     && member.Length > ReplicationMembers.TagPrefix.Length)
            {
                WriteTag(member, node);
            }
            else if (string.Equals(member, ReplicationMembers.Value, StringComparison.Ordinal))
            {
                writer.WriteByte(ReplicationWireFormat.MemberValue);
                WriteValue(node);
            }
            else if (string.Equals(member, ReplicationMembers.PrimaryPart, StringComparison.Ordinal))
            {
                writer.WriteByte(ReplicationWireFormat.MemberPrimaryPart);
                writer.WriteByte(node.Model != null ? (byte)1 : (byte)0);
                if (node.Model != null)
                {
                    writer.WriteVarUInt((ulong)WireId(new InstanceId(node.Model.PrimaryPartId), allowNil: true));
                }
            }
            else if (string.Equals(member, ReplicationMembers.WorldPivot, StringComparison.Ordinal))
            {
                writer.WriteByte(ReplicationWireFormat.MemberWorldPivot);
                if (node.Model == null)
                {
                    writer.WriteByte(0);
                }
                else if (!node.Model.HasStoredWorldPivot)
                {
                    writer.WriteByte(1);
                }
                else
                {
                    writer.WriteByte(2);
                    WriteFloats(node.Model.StoredWorldPivot, 12, "WorldPivot", node.Id);
                }
            }
            else if (string.Equals(member, RbxPlayer.CharacterMember, StringComparison.Ordinal))
            {
                writer.WriteByte(ReplicationWireFormat.MemberCharacter);
                writer.WriteByte(node.Player != null ? (byte)1 : (byte)0);
                if (node.Player != null)
                {
                    writer.WriteVarUInt((ulong)WireId(new InstanceId(node.Player.CharacterId), allowNil: true));
                }
            }
            else if (string.Equals(member, RbxPlayer.DisplayNameMember, StringComparison.Ordinal))
            {
                writer.WriteByte(ReplicationWireFormat.MemberDisplayName);
                writer.WriteString(node.Player?.DisplayName);
            }
            else
            {
                // WHY an unknown member still travels, by name and without a value: the replica's
                // applier reports a member it does not apply, and silence here would hide that the
                // layer which owns the member is missing on one side.
                writer.WriteByte(ReplicationWireFormat.MemberOther);
                writer.WriteString(member);
            }
        }

        private void WriteAttribute(string member, InstanceSnapshot node)
        {
            ReadOnlySpan<char> name = member.AsSpan(ReplicationMembers.AttributePrefix.Length);
            AttributeSnapshot carried = null;
            if (node.Attributes != null)
            {
                for (int index = 0; index < node.Attributes.Count; index++)
                {
                    AttributeSnapshot candidate = node.Attributes[index];
                    if (candidate?.Name != null && name.Equals(candidate.Name.AsSpan(), StringComparison.Ordinal))
                    {
                        carried = candidate;
                        break;
                    }
                }
            }

            ReplicationWireWriter writer = _writer;
            if (carried == null)
            {
                writer.WriteByte(ReplicationWireFormat.MemberAttributeCleared);
                writer.WriteString(name.ToString());
                return;
            }

            writer.WriteByte(ReplicationWireFormat.MemberAttributeSet);
            writer.WriteString(carried.Name);
            writer.WriteByte((byte)carried.Kind);
            switch (carried.Kind)
            {
                case AttributeValueKind.String:
                    writer.WriteString(carried.StringValue);
                    break;
                case AttributeValueKind.Number:
                    writer.WriteDouble(carried.NumberValue);
                    break;
                case AttributeValueKind.Bool:
                    writer.WriteByte(carried.BoolValue ? (byte)1 : (byte)0);
                    break;
                case AttributeValueKind.Vector3:
                case AttributeValueKind.Color3:
                    WriteFloats(carried.StringValue, 3, "attribute " + carried.Name, node.Id);
                    break;
                case AttributeValueKind.Vector2:
                    WriteFloats(carried.StringValue, 2, "attribute " + carried.Name, node.Id);
                    break;
                case AttributeValueKind.UDim:
                    WriteUDim(carried, node.Id);
                    break;
                default:
                    throw new ReplicationWireException(ReplicationWireError.MalformedValue,
                        "attribute " + carried.Name + " on instance " + node.Id + " has unknown kind "
                        + carried.Kind);
            }
        }

        private void WriteTag(string member, InstanceSnapshot node)
        {
            ReadOnlySpan<char> tag = member.AsSpan(ReplicationMembers.TagPrefix.Length);
            string carried = null;
            if (node.Tags != null)
            {
                for (int index = 0; index < node.Tags.Count; index++)
                {
                    string candidate = node.Tags[index];
                    if (candidate != null && tag.Equals(candidate.AsSpan(), StringComparison.Ordinal))
                    {
                        carried = candidate;
                        break;
                    }
                }
            }

            ReplicationWireWriter writer = _writer;
            writer.WriteByte(carried != null
                ? ReplicationWireFormat.MemberTagAdded
                : ReplicationWireFormat.MemberTagRemoved);
            writer.WriteString(carried ?? tag.ToString());
        }

        private void WriteValue(InstanceSnapshot node)
        {
            ReplicationWireWriter writer = _writer;
            ValueSnapshot value = node.Value;
            if (value == null)
            {
                writer.WriteByte(ReplicationWireFormat.ValueAbsent);
                return;
            }

            switch (node.ClassName)
            {
                case "IntValue":
                    if (!long.TryParse(value.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out long integer))
                    {
                        throw MalformedValue("IntValue", node.Id);
                    }

                    writer.WriteByte(ReplicationWireFormat.ValueInt);
                    writer.WriteVarInt(integer);
                    break;
                case "NumberValue":
                    if (!double.TryParse(value.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture,
                            out double number))
                    {
                        throw MalformedValue("NumberValue", node.Id);
                    }

                    writer.WriteByte(ReplicationWireFormat.ValueNumber);
                    writer.WriteDouble(number);
                    break;
                case "StringValue":
                    writer.WriteByte(ReplicationWireFormat.ValueString);
                    writer.WriteString(value.StringValue);
                    break;
                case "BoolValue":
                    writer.WriteByte(ReplicationWireFormat.ValueBool);
                    writer.WriteByte(string.Equals(value.StringValue, "true", StringComparison.Ordinal)
                        ? (byte)1
                        : (byte)0);
                    break;
                case "ObjectValue":
                    writer.WriteByte(ReplicationWireFormat.ValueObject);
                    writer.WriteVarUInt((ulong)WireId(new InstanceId(value.ObjectTargetId), allowNil: true));
                    break;
                case "Vector3Value":
                    writer.WriteByte(ReplicationWireFormat.ValueVector3);
                    WriteFloats(value.StringValue, 3, "Vector3Value", node.Id);
                    break;
                case "CFrameValue":
                    writer.WriteByte(ReplicationWireFormat.ValueCFrame);
                    WriteFloats(value.StringValue, 12, "CFrameValue", node.Id);
                    break;
                case "Color3Value":
                    writer.WriteByte(ReplicationWireFormat.ValueColor3);
                    WriteFloats(value.StringValue, 3, "Color3Value", node.Id);
                    break;
                default:
                    throw MalformedValue(node.ClassName, node.Id);
            }
        }

        private void WriteUDim(AttributeSnapshot attribute, ulong nodeId)
        {
            string text = attribute.StringValue ?? "";
            int comma = text.IndexOf(',');
            if (comma < 0
                || !float.TryParse(text.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float scale)
                || !int.TryParse(text.AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int offset))
            {
                throw MalformedValue("UDim attribute " + attribute.Name, nodeId);
            }

            _writer.WriteSingle(scale);
            _writer.WriteVarInt(offset);
        }

        /// <summary>
        /// Writes the comma-joined round-trip floats of a captured snapshot as raw float32s, parsed
        /// in place: the values are exact, so the replica rebuilds the very string the server holds.
        /// </summary>
        private void WriteFloats(string text, int expected, string what, ulong nodeId)
        {
            ReadOnlySpan<char> rest = (text ?? "").AsSpan();
            for (int index = 0; index < expected; index++)
            {
                int comma = rest.IndexOf(',');
                bool last = index == expected - 1;
                if (last ? comma >= 0 : comma < 0)
                {
                    throw MalformedValue(what, nodeId);
                }

                ReadOnlySpan<char> component = last ? rest : rest.Slice(0, comma);
                if (!float.TryParse(component, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out _floats[index]))
                {
                    throw MalformedValue(what, nodeId);
                }

                rest = last ? ReadOnlySpan<char>.Empty : rest.Slice(comma + 1);
            }

            for (int index = 0; index < expected; index++)
            {
                _writer.WriteSingle(_floats[index]);
            }
        }

        private static ReplicationWireException MalformedValue(string what, ulong nodeId)
        {
            return new ReplicationWireException(ReplicationWireError.MalformedValue,
                what + " on instance " + nodeId + " is not a value the wire format can carry");
        }

        private static long WireId(InstanceId id, bool allowNil = false)
        {
            if (allowNil && !id.IsValid)
            {
                return 0L;
            }

            if (!id.IsServerAssigned)
            {
                throw new ReplicationWireException(ReplicationWireError.InvalidInstanceId,
                    "instance id " + id.Value + " is not server-assigned and cannot travel");
            }

            return (long)id.Value;
        }
    }
}
