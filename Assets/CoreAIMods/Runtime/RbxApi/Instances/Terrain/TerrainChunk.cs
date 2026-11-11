using System;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// One 16 x 16 x 16 block of cells: a material slot (<see cref="TerrainMaterials"/>) and an
    /// occupancy byte (0 empty, 255 full) per cell. Cell (lx, ly, lz) sits at
    /// <see cref="IndexOf"/> = lx + 16 * lz + 256 * ly, so each horizontal layer is contiguous.
    /// </summary>
    /// <remarks>
    /// WHY layers contiguous: generated worlds are mostly horizontal strata, so a whole layer of one
    /// material is one run for the codec. WHY the chunk can be frozen: a snapshot (the P3 gate,
    /// undo) shares the chunk instead of copying it, and the store clones a frozen chunk the first
    /// time it writes to it, so taking a snapshot costs one reference per chunk.
    /// </remarks>
    public sealed class TerrainChunk
    {
        /// <summary>Cells in one chunk.</summary>
        public const int CellCount = TerrainChunkCoord.Size * TerrainChunkCoord.Size * TerrainChunkCoord.Size;

        private readonly byte[] _materials;
        private readonly byte[] _occupancy;

        internal TerrainChunk(TerrainChunkCoord coord)
        {
            Coord = coord;
            _materials = new byte[CellCount];
            _occupancy = new byte[CellCount];
        }

        private TerrainChunk(TerrainChunk source)
        {
            Coord = source.Coord;
            _materials = (byte[])source._materials.Clone();
            _occupancy = (byte[])source._occupancy.Clone();
            SolidCount = source.SolidCount;
            Revision = source.Revision;
        }

        /// <summary>Where this chunk sits, in chunk units.</summary>
        public TerrainChunkCoord Coord { get; }

        /// <summary>Cells that are not <c>Air</c> (water included).</summary>
        public int SolidCount { get; internal set; }

        /// <summary>Store revision of the last write that changed a cell of this chunk.</summary>
        public long Revision { get; internal set; }

        /// <summary>True while a snapshot shares this chunk; the store never writes a frozen chunk.</summary>
        public bool IsFrozen { get; private set; }

        /// <summary>Per-cell material slots in <see cref="IndexOf"/> order.</summary>
        public ReadOnlySpan<byte> Materials => _materials;

        /// <summary>Per-cell occupancy bytes in <see cref="IndexOf"/> order.</summary>
        public ReadOnlySpan<byte> Occupancy => _occupancy;

        internal byte[] MaterialArray => _materials;

        internal byte[] OccupancyArray => _occupancy;

        /// <summary>The flat index of local cell (lx, ly, lz), each in 0..15.</summary>
        public static int IndexOf(int lx, int ly, int lz)
        {
            return lx + (lz << TerrainChunkCoord.Shift) + (ly << (2 * TerrainChunkCoord.Shift));
        }

        internal void Freeze()
        {
            IsFrozen = true;
        }

        internal TerrainChunk CloneUnfrozen()
        {
            return new TerrainChunk(this);
        }
    }
}
