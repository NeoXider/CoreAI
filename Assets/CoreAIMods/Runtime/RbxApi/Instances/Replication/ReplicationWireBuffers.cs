using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Mods.Rbx.Instances.Replication
{
    /// <summary>
    /// A reusable little-endian packet buffer: LEB128 varints, zigzag signed varints, raw floats and
    /// strings deduplicated per packet. Growing stops at the packet cap with a
    /// <see cref="ReplicationWireException"/>, before a byte past it is written.
    /// </summary>
    internal sealed class ReplicationWireWriter
    {
        private static readonly UTF8Encoding Utf8 = new(false, false);

        private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
        private readonly int _maxBytes;
        private byte[] _buffer;
        private int _length;

        public ReplicationWireWriter(int maxBytes)
        {
            if (maxBytes < 16)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes,
                    "A replication packet cap below 16 bytes cannot hold a header.");
            }

            _maxBytes = maxBytes;
            _buffer = new byte[Math.Min(256, maxBytes)];
        }

        public int Length => _length;

        public ArraySegment<byte> Written => new(_buffer, 0, _length);

        public void Reset()
        {
            _length = 0;
            _strings.Clear();
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            _buffer[_length++] = value;
        }

        public void WriteVarUInt(ulong value)
        {
            Ensure(VarUIntSize(value));
            while (value >= 0x80UL)
            {
                _buffer[_length++] = (byte)(value | 0x80UL);
                value >>= 7;
            }

            _buffer[_length++] = (byte)value;
        }

        public void WriteVarInt(long value)
        {
            WriteVarUInt(ZigZag(value));
        }

        public void WriteSingle(float value)
        {
            Ensure(4);
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(_buffer, _length, 4),
                BitConverter.SingleToInt32Bits(value));
            _length += 4;
        }

        public void WriteDouble(double value)
        {
            Ensure(8);
            BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(_buffer, _length, 8),
                BitConverter.DoubleToInt64Bits(value));
            _length += 8;
        }

        public void WriteUInt48(ulong value)
        {
            Ensure(6);
            for (int index = 0; index < 6; index++)
            {
                _buffer[_length++] = (byte)(value >> (8 * index));
            }
        }

        /// <summary>
        /// Writes a string reference: 0 is nil, 1 is a new string inline (byte length, UTF-8) that
        /// later references reuse, n >= 2 is the (n - 2)th string this packet already carried.
        /// </summary>
        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteVarUInt(0UL);
                return;
            }

            if (_strings.TryGetValue(value, out int index))
            {
                WriteVarUInt((ulong)index + 2UL);
                return;
            }

            int byteCount = Utf8.GetByteCount(value);
            WriteVarUInt(1UL);
            WriteVarUInt((ulong)byteCount);
            Ensure(byteCount);
            _length += Utf8.GetBytes(value, 0, value.Length, _buffer, _length);
            _strings.Add(value, _strings.Count);
        }

        internal static ulong ZigZag(long value)
        {
            return (ulong)((value << 1) ^ (value >> 63));
        }

        internal static int VarUIntSize(ulong value)
        {
            int size = 1;
            while (value >= 0x80UL)
            {
                value >>= 7;
                size++;
            }

            return size;
        }

        private void Ensure(int count)
        {
            if (count > _maxBytes - _length)
            {
                throw new ReplicationWireException(ReplicationWireError.PacketTooLarge,
                    "the replication batch does not fit the " + _maxBytes + "-byte packet cap");
            }

            int needed = _length + count;
            if (needed <= _buffer.Length)
            {
                return;
            }

            int grown = _buffer.Length;
            while (grown < needed)
            {
                grown = grown > _maxBytes / 2 ? _maxBytes : grown * 2;
            }

            Array.Resize(ref _buffer, grown);
        }
    }

    /// <summary>
    /// A forward-only cursor over one packet. Every read checks the bytes that remain before it
    /// touches them and returns false with the first <see cref="Error"/> instead of throwing, so a
    /// malformed packet costs nothing beyond what it actually contained.
    /// </summary>
    internal ref struct ReplicationWireReader
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public ReplicationWireReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
            Error = ReplicationWireError.None;
            Detail = null;
        }

        public ReplicationWireError Error { get; private set; }

        public string Detail { get; private set; }

        public int Position => _position;

        public int Remaining => _data.Length - _position;

        public bool Fail(ReplicationWireError error, string detail)
        {
            if (Error == ReplicationWireError.None)
            {
                Error = error;
                Detail = detail + " (at byte " + _position + ")";
            }

            return false;
        }

        public bool TryReadByte(out byte value)
        {
            if (_position >= _data.Length)
            {
                value = 0;
                return Fail(ReplicationWireError.Truncated, "the packet ends inside a field");
            }

            value = _data[_position++];
            return true;
        }

        /// <summary>Reads a canonical LEB128 varint: at most ten bytes, no overlong encodings.</summary>
        public bool TryReadVarUInt(out ulong value)
        {
            value = 0UL;
            for (int shift = 0; shift < 70; shift += 7)
            {
                if (!TryReadByte(out byte next))
                {
                    return false;
                }

                if (shift == 63 && next > 1)
                {
                    return Fail(ReplicationWireError.MalformedVarint, "a varint overflows 64 bits");
                }

                value |= (ulong)(next & 0x7F) << shift;
                if ((next & 0x80) == 0)
                {
                    // WHY overlong forms are refused: one value must have one encoding, or golden
                    // bytes and the size bar stop meaning anything and two decoders can disagree.
                    if (next == 0 && shift > 0)
                    {
                        return Fail(ReplicationWireError.MalformedVarint, "a varint is not minimally encoded");
                    }

                    return true;
                }
            }

            return Fail(ReplicationWireError.MalformedVarint, "a varint runs past ten bytes");
        }

        public bool TryReadVarInt(out long value)
        {
            if (!TryReadVarUInt(out ulong raw))
            {
                value = 0L;
                return false;
            }

            value = (long)(raw >> 1) ^ -(long)(raw & 1UL);
            return true;
        }

        /// <summary>
        /// Reads a count of items that each occupy at least <paramref name="minimumItemBytes"/>; a
        /// count the remaining bytes cannot hold is refused before anything is sized by it.
        /// </summary>
        public bool TryReadCount(int minimumItemBytes, out int count)
        {
            count = 0;
            if (!TryReadVarUInt(out ulong raw))
            {
                return false;
            }

            if (raw > (ulong)(Remaining / minimumItemBytes))
            {
                return Fail(ReplicationWireError.CountExceedsPacket,
                    "a count of " + raw + " cannot fit the " + Remaining + " bytes that remain");
            }

            count = (int)raw;
            return true;
        }

        public bool TryReadSingle(out float value)
        {
            if (Remaining < 4)
            {
                value = 0f;
                return Fail(ReplicationWireError.Truncated, "the packet ends inside a float");
            }

            value = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_position, 4)));
            _position += 4;
            return true;
        }

        public bool TryReadDouble(out double value)
        {
            if (Remaining < 8)
            {
                value = 0d;
                return Fail(ReplicationWireError.Truncated, "the packet ends inside a double");
            }

            value = BitConverter.Int64BitsToDouble(
                BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_position, 8)));
            _position += 8;
            return true;
        }

        public bool TryReadUInt48(out ulong value)
        {
            value = 0UL;
            if (Remaining < 6)
            {
                return Fail(ReplicationWireError.Truncated, "the packet ends inside a rotation");
            }

            for (int index = 0; index < 6; index++)
            {
                value |= (ulong)_data[_position++] << (8 * index);
            }

            return true;
        }

        /// <summary>Reads a string reference (see <see cref="ReplicationWireWriter.WriteString"/>).</summary>
        public bool TryReadString(List<string> table, out string value)
        {
            value = null;
            if (!TryReadVarUInt(out ulong reference))
            {
                return false;
            }

            if (reference == 0UL)
            {
                return true;
            }

            if (reference >= 2UL)
            {
                if (reference - 2UL >= (ulong)table.Count)
                {
                    return Fail(ReplicationWireError.MalformedString,
                        "a string reference names entry " + (reference - 2UL) + " of " + table.Count);
                }

                value = table[(int)(reference - 2UL)];
                return true;
            }

            if (!TryReadVarUInt(out ulong length))
            {
                return false;
            }

            if (length > (ulong)Remaining)
            {
                return Fail(ReplicationWireError.Truncated,
                    "a string of " + length + " bytes runs past the packet");
            }

            try
            {
                value = StrictUtf8.GetString(_data.Slice(_position, (int)length));
            }
            catch (DecoderFallbackException)
            {
                return Fail(ReplicationWireError.MalformedString, "a string is not valid UTF-8");
            }

            _position += (int)length;
            table.Add(value);
            return true;
        }
    }
}
