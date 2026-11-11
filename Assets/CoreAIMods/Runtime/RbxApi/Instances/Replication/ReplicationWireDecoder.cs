using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>The part members one record of a decoded batch carried, for the replica's part layer.</summary>
    public readonly struct ReplicationWirePartEntry
    {
        public ReplicationWirePartEntry(InstanceId instanceId, ReplicationPartState state)
        {
            InstanceId = instanceId;
            State = state;
        }

        public InstanceId InstanceId { get; }

        public ReplicationPartState State { get; }
    }

    /// <summary>
    /// One decoded packet: the plan the engine-free applier applies, the node state it reads through
    /// <see cref="IReplicationStateSource"/>, and the part members the part layer applies afterwards.
    /// </summary>
    /// <remarks>
    /// WHY part members are not in the plan's member lists: the engine-free applier reports and skips
    /// every member it does not own, so a CFrame in the plan would cost one diagnostic per moving part
    /// per tick and still move nothing. They ride beside the plan, in batch order, and reach the
    /// replica through <see cref="IReplicationPartSink"/> once the batch applied.
    /// </remarks>
    public sealed class ReplicationWireBatch : IReplicationStateSource
    {
        private static readonly List<ReplicationWirePartEntry> NoParts = new(0);

        private readonly List<InstanceSnapshot> _states;
        private readonly List<ReplicationWirePartEntry> _parts;
        private readonly InstanceSnapshot _bare = new();
        private int _cursor;

        internal ReplicationWireBatch(ReplicationBatchPlan plan, bool isWorld, List<InstanceSnapshot> states,
            List<ReplicationWirePartEntry> parts, int byteCount)
        {
            Plan = plan;
            IsWorld = isWorld;
            _states = states;
            _parts = parts ?? NoParts;
            ByteCount = byteCount;
        }

        public ReplicationBatchPlan Plan { get; }

        public long Sequence => Plan.Sequence;

        /// <summary>True for the answer to a join or a resync: the replica arms <see cref="ReplicationApplier.BeginResync"/> for it.</summary>
        public bool IsWorld { get; }

        public int ByteCount { get; }

        public int PartStateCount => _parts.Count;

        public ReplicationWirePartEntry GetPartState(int index)
        {
            return _parts[index];
        }

        /// <summary>The part members the batch carried for an instance; false when it carried none.</summary>
        public bool TryGetPartState(InstanceId id, out ReplicationPartState state)
        {
            for (int index = 0; index < _parts.Count; index++)
            {
                if (_parts[index].InstanceId == id)
                {
                    state = _parts[index].State;
                    return true;
                }
            }

            state = default;
            return false;
        }

        /// <summary>
        /// The node state that travelled for a spawn or a patch; null for an id the batch does not
        /// spawn or patch.
        /// </summary>
        public InstanceSnapshot Describe(InstanceId id)
        {
            IReadOnlyList<ReplicationOperation> operations = Plan.Operations;
            int count = operations.Count;
            for (int step = 0; step < count; step++)
            {
                int index = (_cursor + step) % count;
                ReplicationOperation operation = operations[index];
                if (operation.InstanceId != id || operation.Kind == ReplicationOperationKind.Remove)
                {
                    continue;
                }

                _cursor = index + 1 == count ? 0 : index + 1;
                InstanceSnapshot state = _states[index];
                if (state != null)
                {
                    return state;
                }

                // HACK: a patch that names no engine-free member is described by one reused node whose
                // id is set per call. The applier reads only the id of such a node and keeps no
                // reference to it, and a fresh node per moving part would cost three objects per
                // patch on the hottest path of the decoder.
                _bare.Id = id.Value;
                _bare.Revision = operation.Revision;
                return _bare;
            }

            return null;
        }
    }

    /// <summary>
    /// Reads packets written by <see cref="ReplicationWireEncoder"/>. Tolerant: a packet that is
    /// empty, oversized, truncated, of another version or otherwise malformed is refused with a
    /// <see cref="ReplicationWireError"/>; nothing is thrown, and nothing is sized by a count the
    /// packet's own bytes cannot back.
    /// </summary>
    /// <remarks>
    /// WHY the size cap is checked before the first byte is read: it is the same promise the remote
    /// codec makes with its <c>MaxPayloadBytes</c> — an oversized packet costs the receiver the
    /// comparison, not a parse. Past it, every list grows only as records actually parse, so a packet
    /// costs at most a small multiple of its own length however large the counts it claims.
    /// </remarks>
    public sealed class ReplicationWireDecoder
    {
        private const int MinimumRecordBytes = 2;
        private const int MinimumEntryBytes = 2;
        // WHY a cap on the first allocation and not the claimed count: an honest packet of up to 256
        // records is sized once, and a packet that lies about its count pays at most this much
        // before its records run out.
        private const int MaxInitialCapacity = 256;
        private const int InitialMemberCapacity = 16;

        public ReplicationWireDecoder(int maxPacketBytes = ReplicationWireFormat.DefaultMaxPacketBytes)
        {
            if (maxPacketBytes < 16)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPacketBytes), maxPacketBytes,
                    "A replication packet cap below 16 bytes cannot hold a header.");
            }

            MaxPacketBytes = maxPacketBytes;
        }

        public int MaxPacketBytes { get; }

        /// <summary>
        /// Decodes one packet addressed to <paramref name="recipientActorId"/>, or refuses it. On
        /// refusal <paramref name="batch"/> is null and <paramref name="detail"/> says what and where.
        /// </summary>
        public bool TryDecode(ReadOnlySpan<byte> packet, string recipientActorId, out ReplicationWireBatch batch,
            out ReplicationWireError error, out string detail)
        {
            batch = null;
            if (packet.Length == 0)
            {
                error = ReplicationWireError.Empty;
                detail = "the packet is empty";
                return false;
            }

            if (packet.Length > MaxPacketBytes)
            {
                error = ReplicationWireError.PacketTooLarge;
                detail = "the packet has " + packet.Length + " bytes; the cap is " + MaxPacketBytes;
                return false;
            }

            ReplicationWireReader reader = new(packet);
            try
            {
                batch = Decode(ref reader, packet.Length, recipientActorId);
            }
            catch (Exception exception)
            {
                // WHY a fault in the decoder itself is a refused packet and not a crash: every read is
                // bounds-checked, so reaching this is a decoder bug, and a client must not die of one
                // packet. The exception text is kept in the detail so the bug stays visible.
                batch = null;
                error = ReplicationWireError.MalformedValue;
                detail = "the decoder failed (" + exception.GetType().Name + "): " + exception.Message;
                return false;
            }

            error = reader.Error;
            detail = reader.Detail;
            if (batch == null && error == ReplicationWireError.None)
            {
                error = ReplicationWireError.MalformedValue;
                detail = "the packet could not be decoded";
            }

            return batch != null;
        }

        private static ReplicationWireBatch Decode(ref ReplicationWireReader reader, int byteCount,
            string recipientActorId)
        {
            if (!reader.TryReadByte(out byte magic))
            {
                return null;
            }

            if (magic != ReplicationWireFormat.Magic)
            {
                reader.Fail(ReplicationWireError.BadMagic, "the packet is not a replication packet");
                return null;
            }

            if (!reader.TryReadByte(out byte version))
            {
                return null;
            }

            if (version != ReplicationWireFormat.Version)
            {
                reader.Fail(ReplicationWireError.UnsupportedVersion,
                    "the packet is format version " + version + "; this build reads version "
                    + ReplicationWireFormat.Version);
                return null;
            }

            if (!reader.TryReadByte(out byte flags))
            {
                return null;
            }

            if ((flags & ~ReplicationWireFormat.HeaderFlagsKnown) != 0)
            {
                reader.Fail(ReplicationWireError.ReservedBits, "the header sets reserved flags " + flags);
                return null;
            }

            if (!reader.TryReadVarUInt(out ulong sequence))
            {
                return null;
            }

            if (sequence < 1UL || sequence > long.MaxValue)
            {
                reader.Fail(ReplicationWireError.InvalidSequence, "sequence " + sequence + " is out of range");
                return null;
            }

            if (!reader.TryReadCount(MinimumRecordBytes, out int recordCount))
            {
                return null;
            }

            int capacity = Math.Min(recordCount, MaxInitialCapacity);
            List<ReplicationOperation> operations = new(capacity);
            List<InstanceSnapshot> states = new(capacity);
            List<ReplicationWirePartEntry> parts = null;
            List<string> strings = new();
            long previousId = 0L;
            long previousRevision = 0L;
            for (int index = 0; index < recordCount; index++)
            {
                if (!TryReadRecord(ref reader, strings, ref previousId, ref previousRevision,
                        out ReplicationOperation operation, out InstanceSnapshot state, out bool hasPart,
                        out ReplicationPartState part))
                {
                    return null;
                }

                operations.Add(operation);
                states.Add(state);
                if (hasPart)
                {
                    parts ??= new List<ReplicationWirePartEntry>(capacity);
                    parts.Add(new ReplicationWirePartEntry(operation.InstanceId, part));
                }
            }

            if (reader.Remaining != 0)
            {
                reader.Fail(ReplicationWireError.TrailingBytes,
                    reader.Remaining + " byte(s) follow the last record");
                return null;
            }

            ReplicationBatchPlan plan = new(recipientActorId, (long)sequence, operations);
            return new ReplicationWireBatch(plan, (flags & ReplicationWireFormat.HeaderFlagWorld) != 0, states,
                parts, byteCount);
        }

        private static bool TryReadRecord(ref ReplicationWireReader reader, List<string> strings,
            ref long previousId, ref long previousRevision, out ReplicationOperation operation,
            out InstanceSnapshot state, out bool hasPart, out ReplicationPartState part)
        {
            operation = null;
            state = null;
            hasPart = false;
            part = default;
            if (!reader.TryReadByte(out byte head))
            {
                return false;
            }

            int kind = head & ReplicationWireFormat.RecordKindMask;
            int recordFlags = head & ~ReplicationWireFormat.RecordKindMask;
            if (kind < (int)ReplicationWireRecordKind.Spawn || kind > (int)ReplicationWireRecordKind.Remove)
            {
                return reader.Fail(ReplicationWireError.UnknownRecordKind,
                    "record kind " + kind + " is reserved in format version " + ReplicationWireFormat.Version);
            }

            if ((recordFlags & ~ReplicationWireFormat.RecordFlagsKnown) != 0
                || (kind == (int)ReplicationWireRecordKind.Remove && recordFlags != 0))
            {
                return reader.Fail(ReplicationWireError.ReservedBits, "a record sets reserved flags " + head);
            }

            if (!TryReadId(ref reader, ref previousId, out InstanceId id))
            {
                return false;
            }

            if (kind == (int)ReplicationWireRecordKind.Remove)
            {
                operation = new ReplicationOperation(ReplicationOperationKind.Remove, id, 0L);
                return true;
            }

            if (!reader.TryReadVarInt(out long revisionDelta))
            {
                return false;
            }

            long revision = previousRevision + revisionDelta;
            if ((revisionDelta > 0L && revision < previousRevision) || revision < 0L)
            {
                return reader.Fail(ReplicationWireError.InvalidRevision,
                    "instance " + id.Value + " decodes to an invalid revision");
            }

            previousRevision = revision;
            bool spawn = kind == (int)ReplicationWireRecordKind.Spawn;
            if (spawn)
            {
                if (!reader.TryReadVarUInt(out ulong parentId))
                {
                    return false;
                }

                if ((parentId & InstanceId.AuthorityBit) != 0UL)
                {
                    return reader.Fail(ReplicationWireError.InvalidInstanceId,
                        "spawn for instance " + id.Value + " names a parent id that is not server-assigned");
                }

                if (!reader.TryReadString(strings, out string className))
                {
                    return false;
                }

                if (string.IsNullOrEmpty(className))
                {
                    return reader.Fail(ReplicationWireError.MalformedString,
                        "spawn for instance " + id.Value + " names no class");
                }

                state = new InstanceSnapshot
                {
                    Id = id.Value,
                    ParentId = parentId,
                    ClassName = className,
                    Revision = revision
                };
            }

            if ((recordFlags & ReplicationWireFormat.RecordFlagPartBlock) != 0)
            {
                if (!TryReadPart(ref reader, out part))
                {
                    return false;
                }

                hasPart = true;
            }

            List<string> members = null;
            if ((recordFlags & ReplicationWireFormat.RecordFlagMemberList) != 0)
            {
                if (!reader.TryReadCount(MinimumEntryBytes, out int entryCount))
                {
                    return false;
                }

                if (entryCount == 0)
                {
                    return reader.Fail(ReplicationWireError.MalformedValue,
                        "a record flags a member list and carries no entry");
                }

                state ??= new InstanceSnapshot { Id = id.Value, Revision = revision };
                members = new List<string>(Math.Min(entryCount, InitialMemberCapacity));
                for (int entry = 0; entry < entryCount; entry++)
                {
                    if (!TryReadEntry(ref reader, strings, state, members, spawn))
                    {
                        return false;
                    }
                }
            }

            operation = new ReplicationOperation(
                spawn ? ReplicationOperationKind.Spawn : ReplicationOperationKind.Patch, id, revision, members);
            return true;
        }

        private static bool TryReadId(ref ReplicationWireReader reader, ref long previousId, out InstanceId id)
        {
            id = InstanceId.None;
            if (!reader.TryReadVarInt(out long delta))
            {
                return false;
            }

            long value = previousId + delta;
            if ((delta > 0L && value < previousId) || (delta < 0L && value > previousId) || value < 1L)
            {
                return reader.Fail(ReplicationWireError.InvalidInstanceId,
                    "a record decodes to an instance id that is not server-assigned");
            }

            previousId = value;
            id = new InstanceId((ulong)value);
            return true;
        }

        private static bool TryReadPart(ref ReplicationWireReader reader, out ReplicationPartState part)
        {
            part = default;
            if (!reader.TryReadVarUInt(out ulong mask))
            {
                return false;
            }

            const ulong rotationBits = ReplicationWireFormat.PartRotationQuantized | ReplicationWireFormat.PartRotationRaw;
            if (mask == 0UL || (mask & ~(ulong)ReplicationWireFormat.PartBitsKnown) != 0UL
                || ((mask & rotationBits) != 0UL && (mask & ReplicationWireFormat.PartCFrame) == 0UL)
                || ((mask & ReplicationWireFormat.PartAnchoredValue) != 0UL
                    && (mask & ReplicationWireFormat.PartAnchored) == 0UL))
            {
                return reader.Fail(ReplicationWireError.ReservedBits, "a part block sets mask " + mask);
            }

            ReplicationPartFields fields = ReplicationPartFields.None;
            RbxCFrame cframe = RbxCFrame.Identity;
            if ((mask & ReplicationWireFormat.PartCFrame) != 0UL)
            {
                if (!TryReadCFrame(ref reader, mask & rotationBits, out cframe))
                {
                    return false;
                }

                fields |= ReplicationPartFields.CFrame;
            }

            RbxColor3 color = new(0f, 0f, 0f);
            if ((mask & ReplicationWireFormat.PartColor) != 0UL)
            {
                if (!reader.TryReadByte(out byte red) || !reader.TryReadByte(out byte green)
                    || !reader.TryReadByte(out byte blue))
                {
                    return false;
                }

                color = new RbxColor3(ReplicationCFrameQuantizer.DequantizeColorChannel(red),
                    ReplicationCFrameQuantizer.DequantizeColorChannel(green),
                    ReplicationCFrameQuantizer.DequantizeColorChannel(blue));
                fields |= ReplicationPartFields.Color;
            }

            float transparency = 0f;
            if ((mask & ReplicationWireFormat.PartTransparency) != 0UL)
            {
                if (!reader.TryReadSingle(out transparency))
                {
                    return false;
                }

                fields |= ReplicationPartFields.Transparency;
            }

            bool anchored = false;
            if ((mask & ReplicationWireFormat.PartAnchored) != 0UL)
            {
                anchored = (mask & ReplicationWireFormat.PartAnchoredValue) != 0UL;
                fields |= ReplicationPartFields.Anchored;
            }

            part = new ReplicationPartState(fields, cframe, color, transparency, anchored);
            return true;
        }

        private static bool TryReadCFrame(ref ReplicationWireReader reader, ulong rotationBits, out RbxCFrame cframe)
        {
            cframe = RbxCFrame.Identity;
            if (rotationBits == ReplicationWireFormat.PartRotationRaw)
            {
                Span<float> raw = stackalloc float[12];
                for (int index = 0; index < 12; index++)
                {
                    if (!reader.TryReadSingle(out raw[index]))
                    {
                        return false;
                    }
                }

                cframe = new RbxCFrame(raw[0], raw[1], raw[2], raw[3], raw[4], raw[5], raw[6], raw[7], raw[8],
                    raw[9], raw[10], raw[11]);
                return true;
            }

            if (!TryReadPosition(ref reader, out float x) || !TryReadPosition(ref reader, out float y)
                || !TryReadPosition(ref reader, out float z))
            {
                return false;
            }

            if (rotationBits == 0UL)
            {
                cframe = RbxCFrame.FromPosition(x, y, z);
                return true;
            }

            if (rotationBits == ReplicationWireFormat.PartRotationQuantized)
            {
                if (!reader.TryReadUInt48(out ulong packed))
                {
                    return false;
                }

                return ReplicationCFrameQuantizer.TryUnpackRotation(packed, x, y, z, out cframe)
                       || reader.Fail(ReplicationWireError.MalformedCFrame,
                           "a quantized rotation is not a unit quaternion");
            }

            if (!reader.TryReadByte(out byte axisCode))
            {
                return false;
            }

            return ReplicationCFrameQuantizer.TryFromAxisCode(axisCode, x, y, z, out cframe)
                   || reader.Fail(ReplicationWireError.MalformedCFrame,
                       "axis code " + axisCode + " names no rotation");
        }

        private static bool TryReadPosition(ref ReplicationWireReader reader, out float coordinate)
        {
            coordinate = 0f;
            if (!reader.TryReadVarInt(out long fixedValue))
            {
                return false;
            }

            return ReplicationCFrameQuantizer.TryDequantizePosition(fixedValue, out coordinate)
                   || reader.Fail(ReplicationWireError.MalformedCFrame, "a quantized coordinate is out of range");
        }

        private static bool TryReadEntry(ref ReplicationWireReader reader, List<string> strings,
            InstanceSnapshot state, List<string> members, bool spawn)
        {
            if (!reader.TryReadByte(out byte tag))
            {
                return false;
            }

            switch (tag)
            {
                case ReplicationWireFormat.MemberName:
                {
                    if (!reader.TryReadString(strings, out string name))
                    {
                        return false;
                    }

                    if (name == null)
                    {
                        return reader.Fail(ReplicationWireError.MalformedString, "a Name member carries nil");
                    }

                    state.Name = name;
                    members.Add(ReplicationMembers.Name);
                    return true;
                }
                case ReplicationWireFormat.MemberArchivable:
                {
                    if (!TryReadBool(ref reader, out bool archivable))
                    {
                        return false;
                    }

                    state.Archivable = archivable;
                    members.Add(ReplicationMembers.Archivable);
                    return true;
                }
                case ReplicationWireFormat.MemberParent:
                {
                    if (!TryReadReference(ref reader, out ulong parentId))
                    {
                        return false;
                    }

                    state.ParentId = parentId;
                    members.Add(ReplicationMembers.Parent);
                    return true;
                }
                case ReplicationWireFormat.MemberAttributeSet:
                    return TryReadAttribute(ref reader, strings, state, members);
                case ReplicationWireFormat.MemberAttributeCleared:
                {
                    if (!TryReadName(ref reader, strings, "an attribute", out string attribute))
                    {
                        return false;
                    }

                    members.Add(ReplicationMembers.Attribute(attribute));
                    return true;
                }
                case ReplicationWireFormat.MemberTagAdded:
                case ReplicationWireFormat.MemberTagRemoved:
                {
                    if (!TryReadName(ref reader, strings, "a tag", out string tagName))
                    {
                        return false;
                    }

                    if (tag == ReplicationWireFormat.MemberTagAdded)
                    {
                        state.Tags.Add(tagName);
                    }

                    members.Add(ReplicationMembers.Tag(tagName));
                    return true;
                }
                case ReplicationWireFormat.MemberValue:
                    if (!TryReadValue(ref reader, strings, state))
                    {
                        return false;
                    }

                    members.Add(ReplicationMembers.Value);
                    return true;
                case ReplicationWireFormat.MemberPrimaryPart:
                {
                    if (!TryReadBool(ref reader, out bool present))
                    {
                        return false;
                    }

                    if (present)
                    {
                        if (!TryReadReference(ref reader, out ulong primaryPartId))
                        {
                            return false;
                        }

                        state.Model ??= new ModelSnapshot();
                        state.Model.PrimaryPartId = primaryPartId;
                    }

                    members.Add(ReplicationMembers.PrimaryPart);
                    return true;
                }
                case ReplicationWireFormat.MemberWorldPivot:
                    if (!TryReadWorldPivot(ref reader, state))
                    {
                        return false;
                    }

                    members.Add(ReplicationMembers.WorldPivot);
                    return true;
                case ReplicationWireFormat.MemberCharacter:
                {
                    if (!TryReadBool(ref reader, out bool present))
                    {
                        return false;
                    }

                    if (present)
                    {
                        if (!TryReadReference(ref reader, out ulong characterId))
                        {
                            return false;
                        }

                        state.Player ??= new PlayerSnapshot();
                        state.Player.CharacterId = characterId;
                    }

                    members.Add(RbxPlayer.CharacterMember);
                    return true;
                }
                case ReplicationWireFormat.MemberDisplayName:
                {
                    if (!reader.TryReadString(strings, out string displayName))
                    {
                        return false;
                    }

                    if (displayName != null)
                    {
                        state.Player ??= new PlayerSnapshot();
                        state.Player.DisplayName = displayName;
                    }

                    members.Add(RbxPlayer.DisplayNameMember);
                    return true;
                }
                case ReplicationWireFormat.MemberOther:
                {
                    if (!TryReadName(ref reader, strings, "a member", out string member))
                    {
                        return false;
                    }

                    members.Add(member);
                    return true;
                }
                case ReplicationWireFormat.StatePlayerIdentity:
                {
                    if (!spawn)
                    {
                        return reader.Fail(ReplicationWireError.UnknownMemberTag, "a patch carries a Player identity");
                    }

                    if (!TryReadName(ref reader, strings, "a Player actor", out string actorId)
                        || !reader.TryReadVarInt(out long userId))
                    {
                        return false;
                    }

                    state.Player ??= new PlayerSnapshot();
                    state.Player.ActorId = actorId;
                    state.Player.UserId = userId;
                    return true;
                }
                case ReplicationWireFormat.StateClickDetector:
                {
                    if (!spawn)
                    {
                        return reader.Fail(ReplicationWireError.UnknownMemberTag, "a patch carries ClickDetector state");
                    }

                    if (!reader.TryReadString(strings, out string distance))
                    {
                        return false;
                    }

                    state.ClickDetector = new ClickDetectorSnapshot { MaxActivationDistance = distance };
                    return true;
                }
                case ReplicationWireFormat.StateMaterialVariant:
                    return spawn
                        ? TryReadMaterialVariant(ref reader, strings, state)
                        : reader.Fail(ReplicationWireError.UnknownMemberTag, "a patch carries MaterialVariant state");
                case ReplicationWireFormat.StateHumanoid:
                    return spawn
                        ? TryReadHumanoid(ref reader, strings, state)
                        : reader.Fail(ReplicationWireError.UnknownMemberTag, "a patch carries Humanoid state");
                default:
                    return reader.Fail(ReplicationWireError.UnknownMemberTag,
                        "member tag " + tag + " is not defined in format version " + ReplicationWireFormat.Version);
            }
        }

        private static bool TryReadAttribute(ref ReplicationWireReader reader, List<string> strings,
            InstanceSnapshot state, List<string> members)
        {
            if (!TryReadName(ref reader, strings, "an attribute", out string name)
                || !reader.TryReadByte(out byte kindByte))
            {
                return false;
            }

            if (kindByte > (byte)AttributeValueKind.UDim)
            {
                return reader.Fail(ReplicationWireError.MalformedValue,
                    "attribute " + name + " has unknown kind " + kindByte);
            }

            AttributeValueKind kind = (AttributeValueKind)kindByte;
            AttributeSnapshot attribute = new() { Name = name, Kind = kind };
            switch (kind)
            {
                case AttributeValueKind.String:
                {
                    if (!reader.TryReadString(strings, out string text))
                    {
                        return false;
                    }

                    if (text == null)
                    {
                        return reader.Fail(ReplicationWireError.MalformedString,
                            "string attribute " + name + " carries nil");
                    }

                    attribute.StringValue = text;
                    break;
                }
                case AttributeValueKind.Number:
                {
                    if (!reader.TryReadDouble(out double number))
                    {
                        return false;
                    }

                    attribute.NumberValue = number;
                    break;
                }
                case AttributeValueKind.Bool:
                {
                    if (!TryReadBool(ref reader, out bool flag))
                    {
                        return false;
                    }

                    attribute.BoolValue = flag;
                    break;
                }
                case AttributeValueKind.Vector2:
                {
                    if (!TryReadJoinedFloats(ref reader, 2, out string joined))
                    {
                        return false;
                    }

                    attribute.StringValue = joined;
                    break;
                }
                case AttributeValueKind.UDim:
                {
                    if (!reader.TryReadSingle(out float scale) || !reader.TryReadVarInt(out long offset))
                    {
                        return false;
                    }

                    if (offset < int.MinValue || offset > int.MaxValue)
                    {
                        return reader.Fail(ReplicationWireError.MalformedValue,
                            "UDim attribute " + name + " has an offset outside 32 bits");
                    }

                    attribute.StringValue = Format(scale) + "," + offset.ToString(CultureInfo.InvariantCulture);
                    break;
                }
                default:
                {
                    if (!TryReadJoinedFloats(ref reader, 3, out string joined))
                    {
                        return false;
                    }

                    attribute.StringValue = joined;
                    break;
                }
            }

            state.Attributes.Add(attribute);
            members.Add(ReplicationMembers.Attribute(name));
            return true;
        }

        private static bool TryReadValue(ref ReplicationWireReader reader, List<string> strings,
            InstanceSnapshot state)
        {
            if (!reader.TryReadByte(out byte kind))
            {
                return false;
            }

            ValueSnapshot value = new();
            switch (kind)
            {
                case ReplicationWireFormat.ValueAbsent:
                    state.Value = null;
                    return true;
                case ReplicationWireFormat.ValueInt:
                {
                    if (!reader.TryReadVarInt(out long integer))
                    {
                        return false;
                    }

                    value.StringValue = integer.ToString(CultureInfo.InvariantCulture);
                    break;
                }
                case ReplicationWireFormat.ValueNumber:
                {
                    if (!reader.TryReadDouble(out double number))
                    {
                        return false;
                    }

                    value.StringValue = number.ToString("R", CultureInfo.InvariantCulture);
                    break;
                }
                case ReplicationWireFormat.ValueString:
                {
                    if (!reader.TryReadString(strings, out string text))
                    {
                        return false;
                    }

                    value.StringValue = text;
                    break;
                }
                case ReplicationWireFormat.ValueBool:
                {
                    if (!TryReadBool(ref reader, out bool flag))
                    {
                        return false;
                    }

                    value.StringValue = flag ? "true" : "false";
                    break;
                }
                case ReplicationWireFormat.ValueObject:
                {
                    if (!TryReadReference(ref reader, out ulong targetId))
                    {
                        return false;
                    }

                    value.ObjectTargetId = targetId;
                    break;
                }
                case ReplicationWireFormat.ValueVector3:
                case ReplicationWireFormat.ValueColor3:
                {
                    if (!TryReadJoinedFloats(ref reader, 3, out string joined))
                    {
                        return false;
                    }

                    value.StringValue = joined;
                    break;
                }
                case ReplicationWireFormat.ValueCFrame:
                {
                    if (!TryReadJoinedFloats(ref reader, 12, out string joined))
                    {
                        return false;
                    }

                    value.StringValue = joined;
                    break;
                }
                default:
                    return reader.Fail(ReplicationWireError.MalformedValue, "value kind " + kind + " is not defined");
            }

            state.Value = value;
            return true;
        }

        private static bool TryReadWorldPivot(ref ReplicationWireReader reader, InstanceSnapshot state)
        {
            if (!reader.TryReadByte(out byte mode))
            {
                return false;
            }

            switch (mode)
            {
                case 0:
                    return true;
                case 1:
                    state.Model ??= new ModelSnapshot();
                    state.Model.HasStoredWorldPivot = false;
                    state.Model.StoredWorldPivot = null;
                    return true;
                case 2:
                {
                    if (!TryReadJoinedFloats(ref reader, 12, out string joined))
                    {
                        return false;
                    }

                    state.Model ??= new ModelSnapshot();
                    state.Model.HasStoredWorldPivot = true;
                    state.Model.StoredWorldPivot = joined;
                    return true;
                }
                default:
                    return reader.Fail(ReplicationWireError.MalformedValue, "WorldPivot mode " + mode + " is not defined");
            }
        }

        private static bool TryReadMaterialVariant(ref ReplicationWireReader reader, List<string> strings,
            InstanceSnapshot state)
        {
            if (!reader.TryReadString(strings, out string baseMaterial)
                || !reader.TryReadVarInt(out long baseMaterialValue)
                || !reader.TryReadString(strings, out string colorMap)
                || !reader.TryReadString(strings, out string normalMap)
                || !reader.TryReadString(strings, out string roughnessMap)
                || !reader.TryReadString(strings, out string metalnessMap)
                || !reader.TryReadString(strings, out string studsPerTile))
            {
                return false;
            }

            if (baseMaterialValue < int.MinValue || baseMaterialValue > int.MaxValue)
            {
                return reader.Fail(ReplicationWireError.MalformedValue, "a MaterialVariant base value is outside 32 bits");
            }

            state.MaterialVariant = new MaterialVariantSnapshot
            {
                BaseMaterial = baseMaterial,
                BaseMaterialValue = (int)baseMaterialValue,
                ColorMap = colorMap,
                NormalMap = normalMap,
                RoughnessMap = roughnessMap,
                MetalnessMap = metalnessMap,
                StudsPerTile = studsPerTile
            };
            return true;
        }

        private static bool TryReadHumanoid(ref ReplicationWireReader reader, List<string> strings,
            InstanceSnapshot state)
        {
            if (!reader.TryReadString(strings, out string health)
                || !reader.TryReadString(strings, out string maxHealth)
                || !reader.TryReadString(strings, out string walkSpeed)
                || !reader.TryReadString(strings, out string jumpPower)
                || !reader.TryReadString(strings, out string jumpHeight)
                || !TryReadBool(ref reader, out bool useJumpPower)
                || !reader.TryReadString(strings, out string displayName))
            {
                return false;
            }

            state.Humanoid = new HumanoidSnapshot
            {
                Health = health,
                MaxHealth = maxHealth,
                WalkSpeed = walkSpeed,
                JumpPower = jumpPower,
                JumpHeight = jumpHeight,
                UseJumpPower = useJumpPower,
                DisplayName = displayName
            };
            return true;
        }

        private static bool TryReadName(ref ReplicationWireReader reader, List<string> strings, string what,
            out string name)
        {
            if (!reader.TryReadString(strings, out name))
            {
                return false;
            }

            return !string.IsNullOrEmpty(name)
                   || reader.Fail(ReplicationWireError.MalformedString, what + " name is empty");
        }

        private static bool TryReadBool(ref ReplicationWireReader reader, out bool value)
        {
            value = false;
            if (!reader.TryReadByte(out byte raw))
            {
                return false;
            }

            if (raw > 1)
            {
                return reader.Fail(ReplicationWireError.MalformedValue, "a boolean byte is " + raw);
            }

            value = raw == 1;
            return true;
        }

        private static bool TryReadReference(ref ReplicationWireReader reader, out ulong id)
        {
            if (!reader.TryReadVarUInt(out id))
            {
                return false;
            }

            return (id & InstanceId.AuthorityBit) == 0UL
                   || reader.Fail(ReplicationWireError.InvalidInstanceId, "a reference names a local id");
        }

        /// <summary>
        /// Reads float32 components and renders them the way the tree serializer captures them, so the
        /// applier parses back exactly the value the server holds.
        /// </summary>
        private static bool TryReadJoinedFloats(ref ReplicationWireReader reader, int count, out string joined)
        {
            joined = null;
            if (reader.Remaining < count * 4)
            {
                return reader.Fail(ReplicationWireError.Truncated, "the packet ends inside " + count + " floats");
            }

            string[] parts = new string[count];
            for (int index = 0; index < count; index++)
            {
                reader.TryReadSingle(out float component);
                parts[index] = Format(component);
            }

            joined = string.Join(",", parts);
            return true;
        }

        private static string Format(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
