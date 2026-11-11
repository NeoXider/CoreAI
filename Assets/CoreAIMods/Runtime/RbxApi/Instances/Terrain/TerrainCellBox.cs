using System;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// An axis-aligned box of cells: <see cref="MinX"/>.. <see cref="MaxX"/> (exclusive) on each axis.
    /// A box with any size at or below zero is empty.
    /// </summary>
    public readonly struct TerrainCellBox : IEquatable<TerrainCellBox>
    {
        public TerrainCellBox(int minX, int minY, int minZ, int sizeX, int sizeY, int sizeZ)
        {
            MinX = minX;
            MinY = minY;
            MinZ = minZ;
            SizeX = Math.Max(0, sizeX);
            SizeY = Math.Max(0, sizeY);
            SizeZ = Math.Max(0, sizeZ);
        }

        /// <summary>A box from an inclusive minimum and an exclusive maximum corner.</summary>
        public static TerrainCellBox FromMinMax(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            return new TerrainCellBox(minX, minY, minZ, maxX - minX, maxY - minY, maxZ - minZ);
        }

        public int MinX { get; }

        public int MinY { get; }

        public int MinZ { get; }

        public int SizeX { get; }

        public int SizeY { get; }

        public int SizeZ { get; }

        /// <summary>Exclusive end on X.</summary>
        public int MaxX => MinX + SizeX;

        /// <summary>Exclusive end on Y.</summary>
        public int MaxY => MinY + SizeY;

        /// <summary>Exclusive end on Z.</summary>
        public int MaxZ => MinZ + SizeZ;

        /// <summary>Number of cells in the box.</summary>
        public long CellCount => (long)SizeX * SizeY * SizeZ;

        /// <summary>True when the box holds no cell.</summary>
        public bool IsEmpty => SizeX == 0 || SizeY == 0 || SizeZ == 0;

        /// <summary>
        /// The index of cell (x, y, z) in a buffer laid out like the nested Lua arrays of
        /// <c>ReadVoxels</c> (<c>t[x][y][z]</c>): z varies fastest, then y, then x.
        /// </summary>
        public int RegionIndexOf(int x, int y, int z)
        {
            return ((x - MinX) * SizeY + (y - MinY)) * SizeZ + (z - MinZ);
        }

        public bool Equals(TerrainCellBox other)
        {
            return MinX == other.MinX && MinY == other.MinY && MinZ == other.MinZ
                   && SizeX == other.SizeX && SizeY == other.SizeY && SizeZ == other.SizeZ;
        }

        public override bool Equals(object obj)
        {
            return obj is TerrainCellBox other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(MinX, MinY, MinZ, SizeX, SizeY, SizeZ);
        }

        public override string ToString()
        {
            return "cells (" + MinX + ", " + MinY + ", " + MinZ + ") size (" + SizeX + ", " + SizeY + ", "
                   + SizeZ + ")";
        }
    }
}
