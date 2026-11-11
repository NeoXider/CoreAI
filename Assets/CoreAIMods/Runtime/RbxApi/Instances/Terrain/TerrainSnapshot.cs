using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// An immutable view of a <see cref="TerrainChunkStore"/> at one revision. It shares the store's
    /// chunks instead of copying them; the store copies a shared chunk the first time it writes to it,
    /// so a snapshot costs one reference per chunk however large the world is.
    /// </summary>
    public sealed class TerrainSnapshot
    {
        internal TerrainSnapshot(Dictionary<long, TerrainChunk> chunks, long solidCellCount, long revision)
        {
            Chunks = chunks;
            SolidCellCount = solidCellCount;
            Revision = revision;
        }

        /// <summary>Non-empty cells at capture.</summary>
        public long SolidCellCount { get; }

        /// <summary>Store revision at capture.</summary>
        public long Revision { get; }

        /// <summary>Chunks at capture.</summary>
        public int ChunkCount => Chunks.Count;

        internal IReadOnlyDictionary<long, TerrainChunk> Chunks { get; }

        /// <summary>The captured chunk at <paramref name="coord"/>; false where it was all Air.</summary>
        public bool TryGetChunk(TerrainChunkCoord coord, out TerrainChunk chunk)
        {
            return Chunks.TryGetValue(coord.Key, out chunk);
        }
    }
}
