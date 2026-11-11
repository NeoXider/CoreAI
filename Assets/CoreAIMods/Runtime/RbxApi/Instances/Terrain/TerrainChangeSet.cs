using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// What one Terrain write changed, handed to <see cref="TerrainChunkStore.Changed"/> once per
    /// operation (a whole <c>FillBlock</c> or <c>WriteVoxels</c> call is one event, never one per cell).
    /// </summary>
    /// <remarks>
    /// The store reuses this object for every event: a handler that keeps the lists past its own
    /// return must copy them.
    /// </remarks>
    public sealed class TerrainChangeSet
    {
        internal readonly List<TerrainChunkCoord> Changed = new();
        internal readonly List<TerrainChunkCoord> Remesh = new();

        /// <summary>Store revision this operation produced.</summary>
        public long Revision { get; internal set; }

        /// <summary>Cells whose material or occupancy differ after the operation.</summary>
        public long ChangedCellCount { get; internal set; }

        /// <summary>True when the operation replaced the whole store (Clear, snapshot restore, load).</summary>
        public bool IsReset { get; internal set; }

        /// <summary>Chunks with at least one changed cell, a removed (now empty) chunk included.</summary>
        public IReadOnlyList<TerrainChunkCoord> ChangedChunks => Changed;

        /// <summary>
        /// Chunks whose face-culled mesh may differ: every changed chunk plus each existing neighbour
        /// that shares a face with a changed border cell. Nothing else needs a re-mesh.
        /// </summary>
        public IReadOnlyList<TerrainChunkCoord> RemeshChunks => Remesh;

        internal void Reset(long revision)
        {
            Changed.Clear();
            Remesh.Clear();
            Revision = revision;
            ChangedCellCount = 0;
            IsReset = false;
        }
    }
}
