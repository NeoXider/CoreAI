using System;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// World bounds and budgets of one Terrain: the cell box writes may touch, the most non-empty
    /// cells and the most allocated chunks. Two presets ship, <see cref="Desktop"/> and
    /// <see cref="WebGl"/>; a host passes its own through <see cref="TerrainChunkStore.Limits"/>.
    /// </summary>
    /// <remarks>
    /// WHY separate WebGL numbers: a WebGL player has one heap that cannot grow past what the browser
    /// grants, and its world must still save inside the 4 MiB WebGL package budget, so its terrain
    /// stops growing well before a desktop one does. The reference world (256 x 64 x 256 cells,
    /// 1,024 chunks) fits both presets.
    /// </remarks>
    public sealed class TerrainLimits
    {
        public TerrainLimits(int minCellX, int minCellY, int minCellZ, int maxCellX, int maxCellY,
            int maxCellZ, long maxSolidCells, int maxChunks)
        {
            if (minCellX >= maxCellX || minCellY >= maxCellY || minCellZ >= maxCellZ)
            {
                throw new ArgumentException("Terrain bounds need min < max on every axis.");
            }

            const int widest = (1 << 20) * TerrainChunkCoord.Size;
            if (minCellX < -widest || minCellY < -widest || minCellZ < -widest
                || maxCellX > widest || maxCellY > widest || maxCellZ > widest)
            {
                throw new ArgumentOutOfRangeException(nameof(minCellX),
                    "Terrain bounds must stay within +-" + widest + " cells on every axis.");
            }

            if (maxSolidCells <= 0 || maxChunks <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSolidCells),
                    "Terrain budgets must be positive.");
            }

            MinCellX = minCellX;
            MinCellY = minCellY;
            MinCellZ = minCellZ;
            MaxCellX = maxCellX;
            MaxCellY = maxCellY;
            MaxCellZ = maxCellZ;
            MaxSolidCells = maxSolidCells;
            MaxChunks = maxChunks;
        }

        /// <summary>Desktop and Editor preset: +-8,192 cells across, +-1,024 cells up and down,
        /// 16,777,216 non-empty cells in at most 16,384 chunks (about 128 MiB of cell data).</summary>
        public static TerrainLimits Desktop { get; } =
            new(-8192, -1024, -8192, 8192, 1024, 8192, 16_777_216, 16_384);

        /// <summary>WebGL preset: +-2,048 cells across, +-512 up and down, 4,194,304 non-empty
        /// cells in at most 4,096 chunks (about 32 MiB of cell data).</summary>
        public static TerrainLimits WebGl { get; } =
            new(-2048, -512, -2048, 2048, 512, 2048, 4_194_304, 4_096);

        /// <summary>The preset of the platform this assembly was compiled for.</summary>
#if UNITY_WEBGL && !UNITY_EDITOR
        public static TerrainLimits Default => WebGl;
#else
        public static TerrainLimits Default => Desktop;
#endif

        /// <summary>First writable cell on X (inclusive).</summary>
        public int MinCellX { get; }

        /// <summary>First writable cell on Y (inclusive).</summary>
        public int MinCellY { get; }

        /// <summary>First writable cell on Z (inclusive).</summary>
        public int MinCellZ { get; }

        /// <summary>End of the writable cells on X (exclusive).</summary>
        public int MaxCellX { get; }

        /// <summary>End of the writable cells on Y (exclusive).</summary>
        public int MaxCellY { get; }

        /// <summary>End of the writable cells on Z (exclusive).</summary>
        public int MaxCellZ { get; }

        /// <summary>Most cells that may hold a material other than Air.</summary>
        public long MaxSolidCells { get; }

        /// <summary>Most chunks the store may allocate.</summary>
        public int MaxChunks { get; }

        /// <summary>True when cell (x, y, z) lies inside the writable bounds.</summary>
        public bool Contains(int x, int y, int z)
        {
            return x >= MinCellX && x < MaxCellX && y >= MinCellY && y < MaxCellY
                   && z >= MinCellZ && z < MaxCellZ;
        }

        /// <summary>True when the whole box lies inside the writable bounds.</summary>
        public bool Contains(in TerrainCellBox box)
        {
            return box.IsEmpty
                   || (box.MinX >= MinCellX && box.MinY >= MinCellY && box.MinZ >= MinCellZ
                       && box.MaxX <= MaxCellX && box.MaxY <= MaxCellY && box.MaxZ <= MaxCellZ);
        }

        /// <summary>The part of <paramref name="box"/> inside the bounds (possibly empty).</summary>
        public TerrainCellBox Clip(in TerrainCellBox box)
        {
            int minX = Math.Max(box.MinX, MinCellX);
            int minY = Math.Max(box.MinY, MinCellY);
            int minZ = Math.Max(box.MinZ, MinCellZ);
            int maxX = Math.Min(box.MaxX, MaxCellX);
            int maxY = Math.Min(box.MaxY, MaxCellY);
            int maxZ = Math.Min(box.MaxZ, MaxCellZ);
            return TerrainCellBox.FromMinMax(minX, minY, minZ, maxX, maxY, maxZ);
        }

        /// <summary>The bounds in studs, for error messages.</summary>
        public string DescribeStuds()
        {
            return "(" + MinCellX * 4 + ", " + MinCellY * 4 + ", " + MinCellZ * 4 + ") to ("
                   + MaxCellX * 4 + ", " + MaxCellY * 4 + ", " + MaxCellZ * 4 + ") studs";
        }
    }
}
