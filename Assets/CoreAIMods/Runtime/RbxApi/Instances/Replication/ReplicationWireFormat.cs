using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>
    /// The constants of the binary replication wire format, version 1 (<c>dev-docs/REPLICATION_CODEC.md</c>
    /// has the byte layout): framing, limits and the quantization error bounds a replica can rely on.
    /// </summary>
    public static class ReplicationWireFormat
    {
        /// <summary>First byte of every packet; rules out a JSON remote payload or stray bytes.</summary>
        public const byte Magic = 0xCA;

        /// <summary>The format version this build writes and the only one it reads.</summary>
        public const byte Version = 1;

        /// <summary>
        /// The packet cap a decoder applies by default, equal to the remote codec's
        /// <c>MaxPayloadBytes</c>: a larger packet is refused before a single field is read.
        /// </summary>
        public const int DefaultMaxPacketBytes = 65536;

        /// <summary>Position grid: a coordinate travels as a signed count of 1/1024 stud.</summary>
        public const double PositionResolutionStuds = 1d / 1024d;

        /// <summary>
        /// Largest position error per axis for |coordinate| below 8192 studs; at and above 8192 a
        /// float32 coordinate already lies on the grid and travels exactly.
        /// </summary>
        public const double PositionErrorBoundStuds = 1d / 2048d;

        /// <summary>
        /// Coordinates up to this magnitude are quantized; a CFrame with a larger one travels as
        /// twelve raw floats instead.
        /// </summary>
        public const double MaxQuantizedCoordinateStuds = int.MaxValue / 1024d;

        /// <summary>
        /// Largest rotation error of a quantized CFrame, in radians (about 0.0092 degrees): the angle of
        /// the rotation between what the server holds and what the replica decodes. The identity and
        /// the 24 axis-aligned rotations travel exactly.
        /// </summary>
        public const double RotationErrorBoundRadians = 1.6e-4;

        /// <summary>
        /// Largest error per Color channel after clamping to [0, 1]: eight bits per channel, the
        /// precision of Roblox's replicated <c>BasePart.Color3uint8</c>.
        /// </summary>
        public const double ColorErrorBound = 1d / 510d;

        /// <summary>
        /// A rotation matrix whose columns are unit length and pairwise orthogonal within this
        /// tolerance is quantized as the nearest rotation; any other matrix travels raw.
        /// </summary>
        public const float OrthonormalTolerance = 1e-3f;

        internal const byte HeaderFlagWorld = 0x01;
        internal const byte HeaderFlagsKnown = HeaderFlagWorld;

        internal const byte RecordKindMask = 0x0F;
        internal const byte RecordFlagPartBlock = 0x10;
        internal const byte RecordFlagMemberList = 0x20;
        internal const byte RecordFlagsKnown = RecordFlagPartBlock | RecordFlagMemberList;

        internal const uint PartCFrame = 0x01;
        internal const uint PartRotationQuantized = 0x02;
        internal const uint PartRotationRaw = 0x04;
        internal const uint PartColor = 0x08;
        internal const uint PartTransparency = 0x10;
        internal const uint PartAnchored = 0x20;
        internal const uint PartAnchoredValue = 0x40;
        internal const uint PartBitsKnown = 0x7F;

        internal const byte MemberName = 0x01;
        internal const byte MemberArchivable = 0x02;
        internal const byte MemberParent = 0x03;
        internal const byte MemberAttributeSet = 0x04;
        internal const byte MemberAttributeCleared = 0x05;
        internal const byte MemberTagAdded = 0x06;
        internal const byte MemberTagRemoved = 0x07;
        internal const byte MemberValue = 0x08;
        internal const byte MemberPrimaryPart = 0x09;
        internal const byte MemberWorldPivot = 0x0A;
        internal const byte MemberCharacter = 0x0B;
        internal const byte MemberDisplayName = 0x0C;
        internal const byte MemberOther = 0x0D;
        internal const byte StatePlayerIdentity = 0x20;
        internal const byte StateClickDetector = 0x21;
        internal const byte StateMaterialVariant = 0x22;
        internal const byte StateHumanoid = 0x23;

        internal const byte ValueAbsent = 0;
        internal const byte ValueInt = 1;
        internal const byte ValueNumber = 2;
        internal const byte ValueString = 3;
        internal const byte ValueBool = 4;
        internal const byte ValueObject = 5;
        internal const byte ValueVector3 = 6;
        internal const byte ValueCFrame = 7;
        internal const byte ValueColor3 = 8;
    }

    /// <summary>
    /// What a record does. Values 0 and 4 to 15 are reserved: 4 is held for the voxel-terrain chunk
    /// delta, and a decoder of this version refuses a packet carrying any reserved kind, since it
    /// cannot know how long such a record is or what skipping it would leave behind.
    /// </summary>
    public enum ReplicationWireRecordKind : byte
    {
        Spawn = 1,
        Patch = 2,
        Remove = 3
    }

    /// <summary>Why a packet could not be encoded or was refused by the decoder.</summary>
    public enum ReplicationWireError
    {
        None = 0,
        Empty,
        PacketTooLarge,
        BadMagic,
        UnsupportedVersion,
        ReservedBits,
        Truncated,
        MalformedVarint,
        CountExceedsPacket,
        UnknownRecordKind,
        InvalidInstanceId,
        InvalidRevision,
        InvalidSequence,
        UnknownMemberTag,
        MalformedString,
        MalformedValue,
        MalformedCFrame,
        TrailingBytes,
        MissingState,
        InvalidPlan
    }

    /// <summary>An encode-side refusal: the batch cannot be put on the wire as planned.</summary>
    public sealed class ReplicationWireException : Exception
    {
        public ReplicationWireException(ReplicationWireError error, string message)
            : base(message)
        {
            Error = error;
        }

        public ReplicationWireError Error { get; }
    }

    /// <summary>
    /// CFrame quantization: positions on a 1/1024-stud grid, rotations as the identity, one of the 24
    /// axis-aligned rotations, a "smallest three" quaternion of 3 x 15 bits, or twelve raw floats when
    /// the matrix is not a rotation or a value is out of range.
    /// </summary>
    internal static class ReplicationCFrameQuantizer
    {
        internal enum RotationMode
        {
            Identity,
            Quantized,
            Raw,
            AxisAligned
        }

        internal const int RotationBits = 15;

        // WHY an even top level (32766, so 32767 levels) and not 32767: an odd number of levels puts
        // one exactly on zero, so the zero components of a single-axis rotation decode as zero and a
        // part spun about Y never wobbles about X or Z.
        internal const int RotationLevels = (1 << RotationBits) - 2;
        private const ulong RotationFieldMask = (1UL << RotationBits) - 1UL;

        private const double PositionScale = 1024d;
        private const double InverseSqrt2 = 0.70710678118654752440;
        private const double Sqrt2 = 1.41421356237309504880;
        private const long MaxFixed = int.MaxValue;
        private const double MaxUnitSumSlack = 1e-6;

        /// <summary>Picks how a CFrame travels; <paramref name="axisCode"/> is set for an axis-aligned one.</summary>
        internal static RotationMode Classify(in RbxCFrame cframe, out byte axisCode)
        {
            axisCode = 0;
            if (!TryQuantizePosition(cframe.X, out _) || !TryQuantizePosition(cframe.Y, out _)
                || !TryQuantizePosition(cframe.Z, out _))
            {
                return RotationMode.Raw;
            }

            RbxVector3 right = cframe.XVector;
            RbxVector3 up = cframe.YVector;
            RbxVector3 back = cframe.ZVector;
            if (!IsFinite(right) || !IsFinite(up) || !IsFinite(back))
            {
                return RotationMode.Raw;
            }

            if (right.X == 1f && right.Y == 0f && right.Z == 0f
                && up.X == 0f && up.Y == 1f && up.Z == 0f
                && back.X == 0f && back.Y == 0f && back.Z == 1f)
            {
                return RotationMode.Identity;
            }

            if (TryAxisCode(right, up, back, out axisCode))
            {
                return RotationMode.AxisAligned;
            }

            return IsProperOrthonormal(right, up, back) ? RotationMode.Quantized : RotationMode.Raw;
        }

        internal static bool TryQuantizePosition(float value, out long fixedValue)
        {
            fixedValue = 0L;
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return false;
            }

            double scaled = Math.Round(value * PositionScale, MidpointRounding.ToEven);
            if (scaled > MaxFixed || scaled < -MaxFixed)
            {
                return false;
            }

            fixedValue = (long)scaled;
            return true;
        }

        internal static bool TryDequantizePosition(long fixedValue, out float value)
        {
            if (fixedValue > MaxFixed || fixedValue < -MaxFixed)
            {
                value = 0f;
                return false;
            }

            value = (float)(fixedValue / PositionScale);
            return true;
        }

        /// <summary>Packs the rotation into 47 bits: the index of the largest component, then the other three.</summary>
        internal static ulong PackRotation(in RbxCFrame cframe)
        {
            (float qx, float qy, float qz, float qw) = cframe.ToQuaternion();
            double x = qx;
            double y = qy;
            double z = qz;
            double w = qw;
            double length = Math.Sqrt(x * x + y * y + z * z + w * w);
            x /= length;
            y /= length;
            z /= length;
            w /= length;

            int largest = 0;
            double largestAbs = Math.Abs(x);
            if (Math.Abs(y) > largestAbs)
            {
                largest = 1;
                largestAbs = Math.Abs(y);
            }

            if (Math.Abs(z) > largestAbs)
            {
                largest = 2;
                largestAbs = Math.Abs(z);
            }

            if (Math.Abs(w) > largestAbs)
            {
                largest = 3;
            }

            // WHY the largest component is made positive: q and -q are the same rotation, so the
            // decoder can rebuild the dropped component as a non-negative square root.
            if (Component(largest, x, y, z, w) < 0d)
            {
                x = -x;
                y = -y;
                z = -z;
                w = -w;
            }

            ulong bits = (ulong)largest;
            int shift = 2;
            for (int index = 0; index < 4; index++)
            {
                if (index == largest)
                {
                    continue;
                }

                bits |= Quantize(Component(index, x, y, z, w)) << shift;
                shift += RotationBits;
            }

            return bits;
        }

        /// <summary>Rebuilds the rotation <see cref="PackRotation"/> wrote; false for bits no encoder writes.</summary>
        internal static bool TryUnpackRotation(ulong bits, float x, float y, float z, out RbxCFrame cframe)
        {
            cframe = default;
            if ((bits >> (2 + 3 * RotationBits)) != 0UL)
            {
                return false;
            }

            int largest = (int)(bits & 3UL);
            ulong firstLevel = (bits >> 2) & RotationFieldMask;
            ulong secondLevel = (bits >> (2 + RotationBits)) & RotationFieldMask;
            ulong thirdLevel = (bits >> (2 + 2 * RotationBits)) & RotationFieldMask;
            if (firstLevel > RotationLevels || secondLevel > RotationLevels || thirdLevel > RotationLevels)
            {
                return false;
            }

            double first = Dequantize(firstLevel);
            double second = Dequantize(secondLevel);
            double third = Dequantize(thirdLevel);
            double sum = first * first + second * second + third * third;
            if (sum > 1d + MaxUnitSumSlack)
            {
                return false;
            }

            double dropped = Math.Sqrt(Math.Max(0d, 1d - sum));
            double qx;
            double qy;
            double qz;
            double qw;
            switch (largest)
            {
                case 0:
                    qx = dropped;
                    qy = first;
                    qz = second;
                    qw = third;
                    break;
                case 1:
                    qx = first;
                    qy = dropped;
                    qz = second;
                    qw = third;
                    break;
                case 2:
                    qx = first;
                    qy = second;
                    qz = dropped;
                    qw = third;
                    break;
                default:
                    qx = first;
                    qy = second;
                    qz = third;
                    qw = dropped;
                    break;
            }

            cframe = RbxCFrame.FromQuaternion(x, y, z, (float)qx, (float)qy, (float)qz, (float)qw);
            return true;
        }

        /// <summary>
        /// Codes an exactly axis-aligned proper rotation in one byte: bits 0-2 the axis the right
        /// vector lies on, bits 3-5 the axis of the up vector (0 +X, 1 -X, 2 +Y, 3 -Y, 4 +Z, 5 -Z).
        /// </summary>
        internal static bool TryAxisCode(RbxVector3 right, RbxVector3 up, RbxVector3 back, out byte code)
        {
            code = 0;
            if (!TryAxis(right, out int rightAxis) || !TryAxis(up, out int upAxis)
                || rightAxis / 2 == upAxis / 2)
            {
                return false;
            }

            RbxVector3 expectedBack = Cross(AxisVector(rightAxis), AxisVector(upAxis));
            if (back.X != expectedBack.X || back.Y != expectedBack.Y || back.Z != expectedBack.Z)
            {
                return false;
            }

            code = (byte)(rightAxis | (upAxis << 3));
            return true;
        }

        internal static bool TryFromAxisCode(byte code, float x, float y, float z, out RbxCFrame cframe)
        {
            cframe = default;
            int rightAxis = code & 7;
            int upAxis = (code >> 3) & 7;
            if ((code >> 6) != 0 || rightAxis > 5 || upAxis > 5 || rightAxis / 2 == upAxis / 2)
            {
                return false;
            }

            RbxVector3 right = AxisVector(rightAxis);
            RbxVector3 up = AxisVector(upAxis);
            RbxVector3 back = Cross(right, up);
            cframe = new RbxCFrame(x, y, z,
                right.X, up.X, back.X,
                right.Y, up.Y, back.Y,
                right.Z, up.Z, back.Z);
            return true;
        }

        internal static byte QuantizeColorChannel(float channel)
        {
            if (float.IsNaN(channel) || channel <= 0f)
            {
                return 0;
            }

            if (channel >= 1f)
            {
                return 255;
            }

            return (byte)Math.Round(channel * 255d, MidpointRounding.ToEven);
        }

        internal static float DequantizeColorChannel(byte channel)
        {
            return channel / 255f;
        }

        private static ulong Quantize(double component)
        {
            double level = Math.Round((component + InverseSqrt2) / Sqrt2 * RotationLevels,
                MidpointRounding.ToEven);
            if (level < 0d)
            {
                level = 0d;
            }
            else if (level > RotationLevels)
            {
                level = RotationLevels;
            }

            return (ulong)level;
        }

        private static double Dequantize(ulong level)
        {
            return level / (double)RotationLevels * Sqrt2 - InverseSqrt2;
        }

        private static double Component(int index, double x, double y, double z, double w)
        {
            switch (index)
            {
                case 0:
                    return x;
                case 1:
                    return y;
                case 2:
                    return z;
                default:
                    return w;
            }
        }

        private static bool TryAxis(RbxVector3 vector, out int axis)
        {
            axis = -1;
            if (vector.Y == 0f && vector.Z == 0f)
            {
                axis = vector.X == 1f ? 0 : vector.X == -1f ? 1 : -1;
            }
            else if (vector.X == 0f && vector.Z == 0f)
            {
                axis = vector.Y == 1f ? 2 : vector.Y == -1f ? 3 : -1;
            }
            else if (vector.X == 0f && vector.Y == 0f)
            {
                axis = vector.Z == 1f ? 4 : vector.Z == -1f ? 5 : -1;
            }

            return axis >= 0;
        }

        private static RbxVector3 AxisVector(int axis)
        {
            float sign = (axis & 1) == 0 ? 1f : -1f;
            switch (axis / 2)
            {
                case 0:
                    return new RbxVector3(sign, 0f, 0f);
                case 1:
                    return new RbxVector3(0f, sign, 0f);
                default:
                    return new RbxVector3(0f, 0f, sign);
            }
        }

        private static RbxVector3 Cross(RbxVector3 a, RbxVector3 b)
        {
            // WHY + 0f: a product of a zero and a negative axis is -0, and adding +0 turns it into +0,
            // so an axis-aligned CFrame decodes to the same bits a script builds it from.
            return new RbxVector3(
                a.Y * b.Z - a.Z * b.Y + 0f,
                a.Z * b.X - a.X * b.Z + 0f,
                a.X * b.Y - a.Y * b.X + 0f);
        }

        private static bool IsProperOrthonormal(RbxVector3 right, RbxVector3 up, RbxVector3 back)
        {
            float tolerance = ReplicationWireFormat.OrthonormalTolerance;
            if (Math.Abs(Dot(right, right) - 1f) > tolerance || Math.Abs(Dot(up, up) - 1f) > tolerance
                || Math.Abs(Dot(back, back) - 1f) > tolerance || Math.Abs(Dot(right, up)) > tolerance
                || Math.Abs(Dot(right, back)) > tolerance || Math.Abs(Dot(up, back)) > tolerance)
            {
                return false;
            }

            // WHY the determinant: a reflection has orthonormal columns too, and no quaternion can
            // carry it; it must travel raw or the replica would hold its mirror image.
            return Dot(Cross(right, up), back) > 0f;
        }

        private static float Dot(RbxVector3 a, RbxVector3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private static bool IsFinite(RbxVector3 vector)
        {
            return !float.IsNaN(vector.X) && !float.IsInfinity(vector.X)
                   && !float.IsNaN(vector.Y) && !float.IsInfinity(vector.Y)
                   && !float.IsNaN(vector.Z) && !float.IsInfinity(vector.Z);
        }
    }
}
