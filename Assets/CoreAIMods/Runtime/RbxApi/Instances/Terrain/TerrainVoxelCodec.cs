using System;
using System.Collections.Generic;
using System.IO;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// The byte format of a Terrain in a world package (<see cref="PackageEntryName"/>): a header, the
    /// slot-to-<c>Enum.Material</c> palette, then every allocated chunk in
    /// <see cref="TerrainChunkCoord.CompareTo"/> order as run-length-encoded (material, occupancy)
    /// pairs over its 4,096 cells.
    /// </summary>
    /// <remarks>
    /// Layout, little-endian: <c>"CAVX"</c>, u16 version (1), u16 chunk edge (16), u8 palette size P,
    /// P x u16 <c>Enum.Material</c> values, u32 chunk count, then per chunk: zigzag varints X, Y, Z,
    /// varint run count, and per run a varint length, a u8 palette slot and a u8 occupancy.
    /// <para>
    /// The reader is strict about the cells: sorted unique chunks, no all-Air chunk, maximal runs, Air
    /// exactly when occupancy is 0, no trailing bytes. A payload this writer produced therefore decodes
    /// and re-encodes to the same bytes. The palette may list the Terrain materials in any order (a
    /// later writer may extend it), and such a payload re-encodes with this writer's palette.
    /// WHY strict: the package reader never repairs untrusted input, and a canonical form is what makes
    /// a save byte-identical.
    /// </para>
    /// </remarks>
    public static class TerrainVoxelCodec
    {
        /// <summary>The world-package entry that carries the Terrain cells.</summary>
        public const string PackageEntryName = "Terrain/voxels.bin";

        /// <summary>Format version this codec writes and reads.</summary>
        public const ushort FormatVersion = 1;

        private static readonly byte[] Magic = { (byte)'C', (byte)'A', (byte)'V', (byte)'X' };

        /// <summary>True when the store holds any cell; an empty Terrain writes no package entry.</summary>
        public static bool HasContent(TerrainChunkStore store)
        {
            return store != null && store.ChunkCount > 0;
        }

        /// <summary>The canonical bytes of <paramref name="store"/>.</summary>
        public static byte[] Encode(TerrainChunkStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            List<TerrainChunkCoord> coords = new(store.ChunkCount);
            store.CopyChunkCoords(coords);
            using MemoryStream output = new(64 + coords.Count * 64);
            output.Write(Magic, 0, Magic.Length);
            WriteUInt16(output, FormatVersion);
            WriteUInt16(output, TerrainChunkCoord.Size);
            output.WriteByte((byte)TerrainMaterials.SlotCount);
            for (int slot = 0; slot < TerrainMaterials.SlotCount; slot++)
            {
                WriteUInt16(output, (ushort)TerrainMaterials.GetEnumValue((byte)slot));
            }

            WriteUInt32(output, (uint)coords.Count);
            for (int index = 0; index < coords.Count; index++)
            {
                store.TryGetChunk(coords[index], out TerrainChunk chunk);
                WriteChunk(output, chunk);
            }

            return output.ToArray();
        }

        /// <summary>
        /// Replaces the content of <paramref name="store"/> with <paramref name="data"/>. The payload is
        /// fully validated, and checked against the store's bounds and budgets, before the store changes.
        /// </summary>
        /// <exception cref="TerrainVoxelFormatException">The bytes are not a canonical payload.</exception>
        public static void DecodeInto(byte[] data, TerrainChunkStore store)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            List<TerrainChunk> chunks = Decode(data, store.Limits);
            store.ReplaceAll(chunks);
        }

        private static List<TerrainChunk> Decode(byte[] data, TerrainLimits limits)
        {
            Reader reader = new(data);
            for (int index = 0; index < Magic.Length; index++)
            {
                if (reader.ReadByte() != Magic[index])
                {
                    throw new TerrainVoxelFormatException("does not start with the CAVX signature");
                }
            }

            ushort version = reader.ReadUInt16();
            if (version != FormatVersion)
            {
                throw new TerrainVoxelFormatException("has format version " + version + "; this reader reads "
                                                      + FormatVersion);
            }

            ushort edge = reader.ReadUInt16();
            if (edge != TerrainChunkCoord.Size)
            {
                throw new TerrainVoxelFormatException("uses " + edge + "-cell chunks; this reader reads "
                                                      + TerrainChunkCoord.Size);
            }

            int paletteSize = reader.ReadByte();
            if (paletteSize == 0)
            {
                throw new TerrainVoxelFormatException("has an empty material palette");
            }

            byte[] slotOfEntry = new byte[paletteSize];
            bool[] seen = new bool[TerrainMaterials.SlotCount];
            for (int entry = 0; entry < paletteSize; entry++)
            {
                ushort value = reader.ReadUInt16();
                if (!TerrainMaterials.TryGetSlot(value, out byte slot))
                {
                    throw new TerrainVoxelFormatException("names Enum.Material value " + value
                                                          + ", which is not a Terrain material");
                }

                if (seen[slot])
                {
                    throw new TerrainVoxelFormatException("lists Enum.Material." + TerrainMaterials.GetName(slot)
                                                          + " twice in its palette");
                }

                seen[slot] = true;
                slotOfEntry[entry] = slot;
            }

            uint chunkCount = reader.ReadUInt32();
            if (chunkCount > (uint)limits.MaxChunks)
            {
                throw new TerrainVoxelFormatException("holds " + chunkCount + " chunks; the Terrain budget is "
                                                      + limits.MaxChunks);
            }

            List<TerrainChunk> chunks = new((int)chunkCount);
            bool hasPrevious = false;
            TerrainChunkCoord previous = default;
            for (uint index = 0; index < chunkCount; index++)
            {
                TerrainChunkCoord coord = new(reader.ReadZigZag(), reader.ReadZigZag(), reader.ReadZigZag());
                if (!IsAddressable(coord.X) || !IsAddressable(coord.Y) || !IsAddressable(coord.Z))
                {
                    throw new TerrainVoxelFormatException("lists chunk " + coord + ", beyond any Terrain bounds");
                }

                if (hasPrevious && previous.CompareTo(coord) >= 0)
                {
                    throw new TerrainVoxelFormatException("lists chunk " + coord + " out of order or twice");
                }

                hasPrevious = true;
                previous = coord;
                chunks.Add(ReadChunk(ref reader, coord, slotOfEntry));
            }

            if (!reader.AtEnd)
            {
                throw new TerrainVoxelFormatException("has " + reader.Remaining + " bytes after its last chunk");
            }

            return chunks;
        }

        private static bool IsAddressable(int chunkCoordinate)
        {
            return chunkCoordinate >= -(1 << 20) && chunkCoordinate < (1 << 20);
        }

        private static void WriteChunk(Stream output, TerrainChunk chunk)
        {
            WriteZigZag(output, chunk.Coord.X);
            WriteZigZag(output, chunk.Coord.Y);
            WriteZigZag(output, chunk.Coord.Z);
            ReadOnlySpan<byte> materials = chunk.Materials;
            ReadOnlySpan<byte> occupancy = chunk.Occupancy;
            int runs = 0;
            for (int index = 0; index < TerrainChunk.CellCount; index++)
            {
                if (index == 0 || materials[index] != materials[index - 1] || occupancy[index] != occupancy[index - 1])
                {
                    runs++;
                }
            }

            WriteVarUInt(output, (uint)runs);
            int start = 0;
            for (int index = 1; index <= TerrainChunk.CellCount; index++)
            {
                if (index < TerrainChunk.CellCount && materials[index] == materials[start]
                                                   && occupancy[index] == occupancy[start])
                {
                    continue;
                }

                WriteVarUInt(output, (uint)(index - start));
                output.WriteByte(materials[start]);
                output.WriteByte(occupancy[start]);
                start = index;
            }
        }

        private static TerrainChunk ReadChunk(ref Reader reader, TerrainChunkCoord coord, byte[] slotOfEntry)
        {
            uint runCount = reader.ReadVarUInt();
            if (runCount == 0 || runCount > TerrainChunk.CellCount)
            {
                throw new TerrainVoxelFormatException("chunk " + coord + " has " + runCount + " runs");
            }

            TerrainChunk chunk = new(coord);
            byte[] materials = chunk.MaterialArray;
            byte[] occupancy = chunk.OccupancyArray;
            int cell = 0;
            int solid = 0;
            int previousEntry = -1;
            int previousOccupancy = -1;
            for (uint run = 0; run < runCount; run++)
            {
                uint length = reader.ReadVarUInt();
                int entry = reader.ReadByte();
                byte cellOccupancy = reader.ReadByte();
                if (length == 0 || length > TerrainChunk.CellCount - cell)
                {
                    throw new TerrainVoxelFormatException("chunk " + coord + " has a run of " + length
                                                          + " cells where " + (TerrainChunk.CellCount - cell)
                                                          + " remain");
                }

                if (entry >= slotOfEntry.Length)
                {
                    throw new TerrainVoxelFormatException("chunk " + coord + " names palette entry " + entry
                                                          + " of " + slotOfEntry.Length);
                }

                byte slot = slotOfEntry[entry];
                if ((slot == TerrainMaterials.Air) != (cellOccupancy == 0))
                {
                    throw new TerrainVoxelFormatException("chunk " + coord
                                                          + " pairs Air with a non-zero occupancy, or a material with zero");
                }

                if (entry == previousEntry && cellOccupancy == previousOccupancy)
                {
                    throw new TerrainVoxelFormatException("chunk " + coord + " splits one run in two");
                }

                previousEntry = entry;
                previousOccupancy = cellOccupancy;
                int end = cell + (int)length;
                for (; cell < end; cell++)
                {
                    materials[cell] = slot;
                    occupancy[cell] = cellOccupancy;
                }

                if (slot != TerrainMaterials.Air)
                {
                    solid += (int)length;
                }
            }

            if (cell != TerrainChunk.CellCount)
            {
                throw new TerrainVoxelFormatException("chunk " + coord + " covers " + cell + " of "
                                                      + TerrainChunk.CellCount + " cells");
            }

            if (solid == 0)
            {
                throw new TerrainVoxelFormatException("stores chunk " + coord + ", which holds only Air");
            }

            chunk.SolidCount = solid;
            return chunk;
        }

        private static void WriteUInt16(Stream output, ushort value)
        {
            output.WriteByte((byte)value);
            output.WriteByte((byte)(value >> 8));
        }

        private static void WriteUInt32(Stream output, uint value)
        {
            output.WriteByte((byte)value);
            output.WriteByte((byte)(value >> 8));
            output.WriteByte((byte)(value >> 16));
            output.WriteByte((byte)(value >> 24));
        }

        private static void WriteVarUInt(Stream output, uint value)
        {
            while (value >= 0x80)
            {
                output.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            output.WriteByte((byte)value);
        }

        private static void WriteZigZag(Stream output, int value)
        {
            WriteVarUInt(output, (uint)((value << 1) ^ (value >> 31)));
        }

        private struct Reader
        {
            private readonly byte[] _data;
            private int _position;

            public Reader(byte[] data)
            {
                _data = data;
                _position = 0;
            }

            public bool AtEnd => _position == _data.Length;

            public int Remaining => _data.Length - _position;

            public byte ReadByte()
            {
                if (_position >= _data.Length)
                {
                    throw new TerrainVoxelFormatException("ends early, at byte " + _position);
                }

                return _data[_position++];
            }

            public ushort ReadUInt16()
            {
                int low = ReadByte();
                int high = ReadByte();
                return (ushort)(low | (high << 8));
            }

            public uint ReadUInt32()
            {
                uint value = ReadByte();
                value |= (uint)ReadByte() << 8;
                value |= (uint)ReadByte() << 16;
                value |= (uint)ReadByte() << 24;
                return value;
            }

            public uint ReadVarUInt()
            {
                uint value = 0;
                for (int shift = 0; shift < 35; shift += 7)
                {
                    byte next = ReadByte();
                    if (shift == 28 && next > 0x0F)
                    {
                        throw new TerrainVoxelFormatException("has a number wider than 32 bits at byte " + _position);
                    }

                    value |= (uint)(next & 0x7F) << shift;
                    if ((next & 0x80) == 0)
                    {
                        if (next == 0 && shift > 0)
                        {
                            throw new TerrainVoxelFormatException("pads a number at byte " + _position);
                        }

                        return value;
                    }
                }

                throw new TerrainVoxelFormatException("has an unterminated number at byte " + _position);
            }

            public int ReadZigZag()
            {
                uint raw = ReadVarUInt();
                return (int)(raw >> 1) ^ -(int)(raw & 1);
            }
        }
    }

    /// <summary>A Terrain package payload the reader refuses; the message says what is wrong.</summary>
    public sealed class TerrainVoxelFormatException : FormatException
    {
        public TerrainVoxelFormatException(string problem)
            : base("Terrain voxel data " + problem)
        {
        }
    }
}
