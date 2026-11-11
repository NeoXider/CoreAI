using System;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// The position of one 16 x 16 x 16-cell chunk in chunk units. Cell (x, y, z) lives in chunk
    /// (x &gt;&gt; 4, y &gt;&gt; 4, z &gt;&gt; 4), so negative cells floor towards negative chunks.
    /// </summary>
    public readonly struct TerrainChunkCoord : IEquatable<TerrainChunkCoord>, IComparable<TerrainChunkCoord>
    {
        /// <summary>Cells along one chunk edge.</summary>
        public const int Size = 16;

        /// <summary>log2 of <see cref="Size"/>.</summary>
        public const int Shift = 4;

        /// <summary>Mask that keeps the in-chunk part of a cell coordinate.</summary>
        public const int Mask = Size - 1;

        // WHY 21 bits a component: the packed key must fit a long, and the widest preset bounds
        // (TerrainLimits) stay far inside +-2^20 chunks.
        private const int KeyBias = 1 << 20;
        private const long KeyComponentMask = (1L << 21) - 1;

        public TerrainChunkCoord(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }

        public int Y { get; }

        public int Z { get; }

        /// <summary>The first cell of this chunk on the X axis.</summary>
        public int MinCellX => X << Shift;

        /// <summary>The first cell of this chunk on the Y axis.</summary>
        public int MinCellY => Y << Shift;

        /// <summary>The first cell of this chunk on the Z axis.</summary>
        public int MinCellZ => Z << Shift;

        /// <summary>The chunk holding cell (x, y, z).</summary>
        public static TerrainChunkCoord FromCell(int x, int y, int z)
        {
            return new TerrainChunkCoord(x >> Shift, y >> Shift, z >> Shift);
        }

        /// <summary>The coordinate shifted by whole chunks.</summary>
        public TerrainChunkCoord Offset(int dx, int dy, int dz)
        {
            return new TerrainChunkCoord(X + dx, Y + dy, Z + dz);
        }

        /// <summary>A dictionary key unique for every coordinate inside the preset bounds.</summary>
        public long Key =>
            (((long)(X + KeyBias) & KeyComponentMask) << 42)
            | (((long)(Y + KeyBias) & KeyComponentMask) << 21)
            | ((long)(Z + KeyBias) & KeyComponentMask);

        /// <summary>Inverse of <see cref="Key"/>.</summary>
        public static TerrainChunkCoord FromKey(long key)
        {
            return new TerrainChunkCoord(
                (int)((key >> 42) & KeyComponentMask) - KeyBias,
                (int)((key >> 21) & KeyComponentMask) - KeyBias,
                (int)(key & KeyComponentMask) - KeyBias);
        }

        /// <summary>Deterministic order (Y, then Z, then X) used by the codec and every listing.</summary>
        public int CompareTo(TerrainChunkCoord other)
        {
            int byY = Y.CompareTo(other.Y);
            if (byY != 0)
            {
                return byY;
            }

            int byZ = Z.CompareTo(other.Z);
            return byZ != 0 ? byZ : X.CompareTo(other.X);
        }

        public bool Equals(TerrainChunkCoord other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is TerrainChunkCoord other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Z);
        }

        public static bool operator ==(TerrainChunkCoord a, TerrainChunkCoord b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(TerrainChunkCoord a, TerrainChunkCoord b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return "(" + X + ", " + Y + ", " + Z + ")";
        }
    }
}
