using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// The engine-free voxel store behind <c>workspace.Terrain</c>: a sparse map of 16 x 16 x 16-cell
    /// chunks (an all-Air chunk is never allocated), change events, world bounds and budgets.
    /// Every mutation is one operation over a cell box: it either completes or, when it would pass a
    /// budget, changes nothing and raises BUDGET_EXCEEDED.
    /// </summary>
    /// <remarks>
    /// Single-threaded by design (main thread on every platform, WebGL included); a
    /// <see cref="Changed"/> handler must not write to the store it is observing.
    /// <para>
    /// WHY a budget check before the write rather than a rollback after it: most writes pass a cheap
    /// upper bound (current cells plus the box volume), so they run one pass; only a write near the
    /// budget pays a counting pass, and a refused write never touches a cell.
    /// </para>
    /// </remarks>
    public sealed class TerrainChunkStore
    {
        private readonly Dictionary<long, TerrainChunk> _chunks = new();
        private readonly TerrainChangeSet _changeSet = new();
        private readonly List<TouchedChunk> _touched = new();
        private readonly HashSet<long> _remeshKeys = new();
        private TerrainLimits _limits;
        private long _solidCells;
        private long _revision;
        private bool _notifying;

        /// <summary>Creates an empty store under <paramref name="limits"/> (null: <see cref="TerrainLimits.Default"/>).</summary>
        public TerrainChunkStore(TerrainLimits limits = null)
        {
            _limits = limits ?? TerrainLimits.Default;
        }

        /// <summary>
        /// Raised once per operation that changed at least one cell. The argument is reused; copy what
        /// you keep. Nothing is raised for a write that left every cell as it was.
        /// </summary>
        public event Action<TerrainChangeSet> Changed;

        /// <summary>Bounds and budgets; a new value applies to the next write and never drops content.</summary>
        public TerrainLimits Limits
        {
            get => _limits;
            set => _limits = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>Cells holding a material other than Air.</summary>
        public long SolidCellCount => _solidCells;

        /// <summary>Allocated chunks.</summary>
        public int ChunkCount => _chunks.Count;

        /// <summary>Increments once per operation that changed a cell.</summary>
        public long Revision => _revision;

        /// <summary>The chunk at <paramref name="coord"/>; false where every cell is Air.</summary>
        public bool TryGetChunk(TerrainChunkCoord coord, out TerrainChunk chunk)
        {
            return _chunks.TryGetValue(coord.Key, out chunk);
        }

        /// <summary>Material slot and occupancy byte of one cell (Air and 0 where nothing is stored).</summary>
        public void GetCell(int x, int y, int z, out byte material, out byte occupancy)
        {
            if (_chunks.TryGetValue(TerrainChunkCoord.FromCell(x, y, z).Key, out TerrainChunk chunk))
            {
                int index = TerrainChunk.IndexOf(x & TerrainChunkCoord.Mask, y & TerrainChunkCoord.Mask,
                    z & TerrainChunkCoord.Mask);
                material = chunk.MaterialArray[index];
                occupancy = chunk.OccupancyArray[index];
                return;
            }

            material = TerrainMaterials.Air;
            occupancy = 0;
        }

        /// <summary>Material slot of one cell.</summary>
        public byte GetMaterial(int x, int y, int z)
        {
            GetCell(x, y, z, out byte material, out byte _);
            return material;
        }

        /// <summary>Fills the coordinates of every allocated chunk, in <see cref="TerrainChunkCoord.CompareTo"/> order.</summary>
        public void CopyChunkCoords(List<TerrainChunkCoord> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            into.Clear();
            foreach (KeyValuePair<long, TerrainChunk> pair in _chunks)
            {
                into.Add(pair.Value.Coord);
            }

            into.Sort();
        }

        /// <summary>Sets one cell. Out of bounds raises BAD_ARGUMENT.</summary>
        public void SetCell(int x, int y, int z, byte material, byte occupancy)
        {
            TerrainCellBox box = new(x, y, z, 1, 1, 1);
            RequireInBounds(box, "Terrain cell write");
            FillBox(box, material, occupancy);
        }

        /// <summary>Sets every cell of an in-bounds box to one material and occupancy.</summary>
        public void FillBox(in TerrainCellBox box, byte material, byte occupancy)
        {
            RequireSlot(material);
            RequireInBounds(box, "Terrain fill");
            ConstantFill op = new(material, occupancy);
            Apply(box, ref op);
        }

        /// <summary>
        /// Sets the cells of <paramref name="box"/> (clipped to the bounds, never refused for size)
        /// whose centres <paramref name="rasterizer"/> places inside its oriented box.
        /// </summary>
        public void FillOrientedBox(in TerrainBoxRasterizer rasterizer, byte material, byte occupancy)
        {
            RequireSlot(material);
            TerrainCellBox clipped = _limits.Clip(rasterizer.CellBounds);
            OrientedFill op = new(rasterizer, material, occupancy);
            Apply(clipped, ref op);
        }

        /// <summary>
        /// Writes a box from buffers laid out in <see cref="TerrainCellBox.RegionIndexOf"/> order.
        /// Every material must be a valid slot.
        /// </summary>
        public void WriteBox(in TerrainCellBox box, byte[] materials, byte[] occupancy)
        {
            RequireBuffers(box, materials, occupancy, "WriteBox");
            for (long index = 0; index < box.CellCount; index++)
            {
                RequireSlot(materials[index]);
            }

            RequireInBounds(box, "Terrain write");
            BufferWrite op = new(box, materials, occupancy);
            Apply(box, ref op);
        }

        /// <summary>
        /// Reads a box into buffers in <see cref="TerrainCellBox.RegionIndexOf"/> order; cells outside
        /// the bounds or in unallocated chunks read as Air with occupancy 0.
        /// </summary>
        public void ReadBox(in TerrainCellBox box, byte[] materials, byte[] occupancy)
        {
            RequireBuffers(box, materials, occupancy, "ReadBox");
            int count = (int)box.CellCount;
            Array.Clear(materials, 0, count);
            Array.Clear(occupancy, 0, count);
            if (box.IsEmpty)
            {
                return;
            }

            int chunkMinX = box.MinX >> TerrainChunkCoord.Shift;
            int chunkMaxX = (box.MaxX - 1) >> TerrainChunkCoord.Shift;
            int chunkMinY = box.MinY >> TerrainChunkCoord.Shift;
            int chunkMaxY = (box.MaxY - 1) >> TerrainChunkCoord.Shift;
            int chunkMinZ = box.MinZ >> TerrainChunkCoord.Shift;
            int chunkMaxZ = (box.MaxZ - 1) >> TerrainChunkCoord.Shift;
            for (int cy = chunkMinY; cy <= chunkMaxY; cy++)
            {
                for (int cz = chunkMinZ; cz <= chunkMaxZ; cz++)
                {
                    for (int cx = chunkMinX; cx <= chunkMaxX; cx++)
                    {
                        TerrainChunkCoord coord = new(cx, cy, cz);
                        if (!_chunks.TryGetValue(coord.Key, out TerrainChunk chunk))
                        {
                            continue;
                        }

                        byte[] chunkMaterials = chunk.MaterialArray;
                        byte[] chunkOccupancy = chunk.OccupancyArray;
                        int x0 = Math.Max(box.MinX, coord.MinCellX);
                        int x1 = Math.Min(box.MaxX, coord.MinCellX + TerrainChunkCoord.Size);
                        int y0 = Math.Max(box.MinY, coord.MinCellY);
                        int y1 = Math.Min(box.MaxY, coord.MinCellY + TerrainChunkCoord.Size);
                        int z0 = Math.Max(box.MinZ, coord.MinCellZ);
                        int z1 = Math.Min(box.MaxZ, coord.MinCellZ + TerrainChunkCoord.Size);
                        for (int x = x0; x < x1; x++)
                        {
                            for (int y = y0; y < y1; y++)
                            {
                                int regionIndex = box.RegionIndexOf(x, y, z0);
                                int chunkIndex = TerrainChunk.IndexOf(x - coord.MinCellX, y - coord.MinCellY,
                                    z0 - coord.MinCellZ);
                                for (int z = z0; z < z1; z++)
                                {
                                    materials[regionIndex] = chunkMaterials[chunkIndex];
                                    occupancy[regionIndex] = chunkOccupancy[chunkIndex];
                                    regionIndex++;
                                    chunkIndex += TerrainChunkCoord.Size;
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Mirror <c>ReplaceMaterial</c> inside an in-bounds box: cells holding <paramref name="source"/>
        /// become <paramref name="target"/>. Air as the source selects empty cells (filled full);
        /// Air as the target empties the selected cells. Returns the cells changed.
        /// </summary>
        public long ReplaceMaterial(in TerrainCellBox box, byte source, byte target)
        {
            RequireSlot(source);
            RequireSlot(target);
            RequireInBounds(box, "Terrain material replace");
            ReplaceOperation op = new(source, target);
            return Apply(box, ref op);
        }

        /// <summary>Mirror <c>Terrain:Clear()</c>: removes every cell.</summary>
        public void Clear()
        {
            ThrowIfNotifying();
            if (_chunks.Count == 0)
            {
                return;
            }

            _revision++;
            _changeSet.Reset(_revision);
            _changeSet.IsReset = true;
            _changeSet.ChangedCellCount = _solidCells;
            foreach (KeyValuePair<long, TerrainChunk> pair in _chunks)
            {
                _changeSet.Changed.Add(pair.Value.Coord);
            }

            _changeSet.Changed.Sort();
            _changeSet.Remesh.AddRange(_changeSet.Changed);
            _chunks.Clear();
            _solidCells = 0;
            Raise();
        }

        /// <summary>
        /// Shares every chunk with an immutable snapshot (one reference per chunk, no cell copy);
        /// the next write to a shared chunk copies it first.
        /// </summary>
        public TerrainSnapshot CaptureSnapshot()
        {
            Dictionary<long, TerrainChunk> shared = new(_chunks.Count);
            foreach (KeyValuePair<long, TerrainChunk> pair in _chunks)
            {
                pair.Value.Freeze();
                shared.Add(pair.Key, pair.Value);
            }

            return new TerrainSnapshot(shared, _solidCells, _revision);
        }

        /// <summary>
        /// Puts the store back to <paramref name="snapshot"/>. Chunks the snapshot still shares are
        /// adopted as they are, so only chunks that differ are reported changed.
        /// </summary>
        public void RestoreSnapshot(TerrainSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            ThrowIfNotifying();
            _changeSet.Reset(_revision + 1);
            foreach (KeyValuePair<long, TerrainChunk> pair in _chunks)
            {
                if (!snapshot.Chunks.TryGetValue(pair.Key, out TerrainChunk kept)
                    || !ReferenceEquals(kept, pair.Value))
                {
                    _changeSet.Changed.Add(pair.Value.Coord);
                }
            }

            foreach (KeyValuePair<long, TerrainChunk> pair in snapshot.Chunks)
            {
                if (!_chunks.ContainsKey(pair.Key))
                {
                    _changeSet.Changed.Add(pair.Value.Coord);
                }
            }

            _chunks.Clear();
            foreach (KeyValuePair<long, TerrainChunk> pair in snapshot.Chunks)
            {
                _chunks.Add(pair.Key, pair.Value);
            }

            _solidCells = snapshot.SolidCellCount;
            if (_changeSet.Changed.Count == 0)
            {
                return;
            }

            _revision++;
            _changeSet.IsReset = true;
            _changeSet.Changed.Sort();
            _changeSet.Remesh.AddRange(_changeSet.Changed);
            Raise();
        }

        /// <summary>
        /// Replaces the whole content with decoded chunks (the codec's restore path). The chunks are
        /// checked against the bounds and budgets first; a refused load leaves the store untouched.
        /// </summary>
        internal void ReplaceAll(List<TerrainChunk> chunks)
        {
            ThrowIfNotifying();
            long solid = 0;
            for (int index = 0; index < chunks.Count; index++)
            {
                TerrainChunk chunk = chunks[index];
                TerrainChunkCoord coord = chunk.Coord;
                if (!_limits.Contains(coord.MinCellX, coord.MinCellY, coord.MinCellZ)
                    || !_limits.Contains(coord.MinCellX + TerrainChunkCoord.Mask,
                        coord.MinCellY + TerrainChunkCoord.Mask, coord.MinCellZ + TerrainChunkCoord.Mask))
                {
                    throw RbxError.BadArgument(
                        "Terrain chunk " + coord + " lies outside the Terrain bounds " + _limits.DescribeStuds(),
                        "load the world with the limits it was saved under");
                }

                solid += chunk.SolidCount;
            }

            RequireBudget(solid, chunks.Count);
            _changeSet.Reset(_revision + 1);
            foreach (KeyValuePair<long, TerrainChunk> pair in _chunks)
            {
                _changeSet.Changed.Add(pair.Value.Coord);
            }

            _chunks.Clear();
            for (int index = 0; index < chunks.Count; index++)
            {
                TerrainChunk chunk = chunks[index];
                if (!_chunks.ContainsKey(chunk.Coord.Key))
                {
                    _changeSet.Changed.Add(chunk.Coord);
                }

                chunk.Revision = _revision + 1;
                _chunks[chunk.Coord.Key] = chunk;
            }

            _solidCells = solid;
            if (_changeSet.Changed.Count == 0)
            {
                return;
            }

            _revision++;
            _changeSet.IsReset = true;
            DeduplicateAndSort(_changeSet.Changed);
            _changeSet.ChangedCellCount = solid;
            _changeSet.Remesh.AddRange(_changeSet.Changed);
            Raise();
        }

        // ---- The one write loop --------------------------------------------------------------

        private long Apply<TOperation>(in TerrainCellBox box, ref TOperation operation)
            where TOperation : struct, ITerrainCellOperation
        {
            ThrowIfNotifying();
            if (box.IsEmpty)
            {
                return 0;
            }

            if (!operation.OnlyClears && !FitsWithoutCounting(box))
            {
                CountDelta(box, ref operation, out long solidDelta, out int newChunks);
                RequireBudget(_solidCells + solidDelta, _chunks.Count + newChunks);
            }

            long revision = _revision + 1;
            _touched.Clear();
            long changedCells = 0;
            int chunkMinX = box.MinX >> TerrainChunkCoord.Shift;
            int chunkMaxX = (box.MaxX - 1) >> TerrainChunkCoord.Shift;
            int chunkMinY = box.MinY >> TerrainChunkCoord.Shift;
            int chunkMaxY = (box.MaxY - 1) >> TerrainChunkCoord.Shift;
            int chunkMinZ = box.MinZ >> TerrainChunkCoord.Shift;
            int chunkMaxZ = (box.MaxZ - 1) >> TerrainChunkCoord.Shift;
            for (int cy = chunkMinY; cy <= chunkMaxY; cy++)
            {
                for (int cz = chunkMinZ; cz <= chunkMaxZ; cz++)
                {
                    for (int cx = chunkMinX; cx <= chunkMaxX; cx++)
                    {
                        changedCells += ApplyToChunk(new TerrainChunkCoord(cx, cy, cz), box, ref operation,
                            revision);
                    }
                }
            }

            if (changedCells == 0)
            {
                return 0;
            }

            _revision = revision;
            _changeSet.Reset(revision);
            _changeSet.ChangedCellCount = changedCells;
            CollectRemesh();
            Raise();
            return changedCells;
        }

        private long ApplyToChunk<TOperation>(TerrainChunkCoord coord, in TerrainCellBox box,
            ref TOperation operation, long revision)
            where TOperation : struct, ITerrainCellOperation
        {
            long key = coord.Key;
            _chunks.TryGetValue(key, out TerrainChunk chunk);
            bool writable = chunk != null && !chunk.IsFrozen;
            byte[] materials = chunk?.MaterialArray;
            byte[] occupancy = chunk?.OccupancyArray;
            int originX = coord.MinCellX;
            int originY = coord.MinCellY;
            int originZ = coord.MinCellZ;
            int x0 = Math.Max(box.MinX, originX);
            int x1 = Math.Min(box.MaxX, originX + TerrainChunkCoord.Size);
            int y0 = Math.Max(box.MinY, originY);
            int y1 = Math.Min(box.MaxY, originY + TerrainChunkCoord.Size);
            int z0 = Math.Max(box.MinZ, originZ);
            int z1 = Math.Min(box.MaxZ, originZ + TerrainChunkCoord.Size);
            int changed = 0;
            int solidDelta = 0;
            int minX = TerrainChunkCoord.Size;
            int minY = TerrainChunkCoord.Size;
            int minZ = TerrainChunkCoord.Size;
            int maxX = -1;
            int maxY = -1;
            int maxZ = -1;
            for (int x = x0; x < x1; x++)
            {
                int lx = x - originX;
                for (int y = y0; y < y1; y++)
                {
                    int ly = y - originY;
                    for (int z = z0; z < z1; z++)
                    {
                        int lz = z - originZ;
                        int index = TerrainChunk.IndexOf(lx, ly, lz);
                        byte oldMaterial = materials != null ? materials[index] : TerrainMaterials.Air;
                        byte oldOccupancy = occupancy != null ? occupancy[index] : (byte)0;
                        if (!operation.Select(x, y, z, oldMaterial, oldOccupancy, out byte newMaterial,
                                out byte newOccupancy))
                        {
                            continue;
                        }

                        Normalize(ref newMaterial, ref newOccupancy);
                        if (newMaterial == oldMaterial && newOccupancy == oldOccupancy)
                        {
                            continue;
                        }

                        if (!writable)
                        {
                            chunk = MakeWritable(coord, chunk);
                            writable = true;
                            materials = chunk.MaterialArray;
                            occupancy = chunk.OccupancyArray;
                        }

                        materials[index] = newMaterial;
                        occupancy[index] = newOccupancy;
                        if (oldMaterial == TerrainMaterials.Air)
                        {
                            solidDelta++;
                        }
                        else if (newMaterial == TerrainMaterials.Air)
                        {
                            solidDelta--;
                        }

                        changed++;
                        minX = Math.Min(minX, lx);
                        minY = Math.Min(minY, ly);
                        minZ = Math.Min(minZ, lz);
                        maxX = Math.Max(maxX, lx);
                        maxY = Math.Max(maxY, ly);
                        maxZ = Math.Max(maxZ, lz);
                    }
                }
            }

            if (changed == 0)
            {
                return 0;
            }

            chunk.SolidCount += solidDelta;
            chunk.Revision = revision;
            _solidCells += solidDelta;
            if (chunk.SolidCount == 0)
            {
                _chunks.Remove(key);
            }

            _touched.Add(new TouchedChunk(coord, minX, minY, minZ, maxX, maxY, maxZ));
            return changed;
        }

        private void CountDelta<TOperation>(in TerrainCellBox box, ref TOperation operation,
            out long solidDelta, out int newChunks)
            where TOperation : struct, ITerrainCellOperation
        {
            solidDelta = 0;
            newChunks = 0;
            int chunkMinX = box.MinX >> TerrainChunkCoord.Shift;
            int chunkMaxX = (box.MaxX - 1) >> TerrainChunkCoord.Shift;
            int chunkMinY = box.MinY >> TerrainChunkCoord.Shift;
            int chunkMaxY = (box.MaxY - 1) >> TerrainChunkCoord.Shift;
            int chunkMinZ = box.MinZ >> TerrainChunkCoord.Shift;
            int chunkMaxZ = (box.MaxZ - 1) >> TerrainChunkCoord.Shift;
            for (int cy = chunkMinY; cy <= chunkMaxY; cy++)
            {
                for (int cz = chunkMinZ; cz <= chunkMaxZ; cz++)
                {
                    for (int cx = chunkMinX; cx <= chunkMaxX; cx++)
                    {
                        TerrainChunkCoord coord = new(cx, cy, cz);
                        _chunks.TryGetValue(coord.Key, out TerrainChunk chunk);
                        byte[] materials = chunk?.MaterialArray;
                        byte[] occupancy = chunk?.OccupancyArray;
                        int x0 = Math.Max(box.MinX, coord.MinCellX);
                        int x1 = Math.Min(box.MaxX, coord.MinCellX + TerrainChunkCoord.Size);
                        int y0 = Math.Max(box.MinY, coord.MinCellY);
                        int y1 = Math.Min(box.MaxY, coord.MinCellY + TerrainChunkCoord.Size);
                        int z0 = Math.Max(box.MinZ, coord.MinCellZ);
                        int z1 = Math.Min(box.MaxZ, coord.MinCellZ + TerrainChunkCoord.Size);
                        bool gainsSolid = false;
                        for (int x = x0; x < x1; x++)
                        {
                            for (int y = y0; y < y1; y++)
                            {
                                for (int z = z0; z < z1; z++)
                                {
                                    int index = TerrainChunk.IndexOf(x - coord.MinCellX, y - coord.MinCellY,
                                        z - coord.MinCellZ);
                                    byte oldMaterial = materials != null ? materials[index] : TerrainMaterials.Air;
                                    byte oldOccupancy = occupancy != null ? occupancy[index] : (byte)0;
                                    if (!operation.Select(x, y, z, oldMaterial, oldOccupancy,
                                            out byte newMaterial, out byte newOccupancy))
                                    {
                                        continue;
                                    }

                                    Normalize(ref newMaterial, ref newOccupancy);
                                    bool wasSolid = oldMaterial != TerrainMaterials.Air;
                                    bool isSolid = newMaterial != TerrainMaterials.Air;
                                    if (wasSolid != isSolid)
                                    {
                                        solidDelta += isSolid ? 1 : -1;
                                    }

                                    gainsSolid |= isSolid;
                                }
                            }
                        }

                        if (chunk == null && gainsSolid)
                        {
                            newChunks++;
                        }
                    }
                }
            }
        }

        private bool FitsWithoutCounting(in TerrainCellBox box)
        {
            long spannedChunks =
                (long)(((box.MaxX - 1) >> TerrainChunkCoord.Shift) - (box.MinX >> TerrainChunkCoord.Shift) + 1)
                * (((box.MaxY - 1) >> TerrainChunkCoord.Shift) - (box.MinY >> TerrainChunkCoord.Shift) + 1)
                * (((box.MaxZ - 1) >> TerrainChunkCoord.Shift) - (box.MinZ >> TerrainChunkCoord.Shift) + 1);
            return _solidCells + box.CellCount <= _limits.MaxSolidCells
                   && _chunks.Count + spannedChunks <= _limits.MaxChunks;
        }

        private void RequireBudget(long solidCells, long chunks)
        {
            if (solidCells > _limits.MaxSolidCells)
            {
                throw new RbxError(RbxErrorCode.BudgetExceeded,
                    "Terrain write refused: it would leave " + solidCells + " non-empty cells and the "
                    + "Terrain budget is " + _limits.MaxSolidCells,
                    "clear unused cells first (terrain:FillBlock(cframe, size, Enum.Material.Air) or "
                    + "terrain:Clear()), or build a smaller area");
            }

            if (chunks > _limits.MaxChunks)
            {
                throw new RbxError(RbxErrorCode.BudgetExceeded,
                    "Terrain write refused: it would need " + chunks + " chunks of 16x16x16 cells and the "
                    + "Terrain budget is " + _limits.MaxChunks,
                    "keep the build in a more compact area, or clear cells far from it first");
            }
        }

        private TerrainChunk MakeWritable(TerrainChunkCoord coord, TerrainChunk current)
        {
            TerrainChunk writable = current == null ? new TerrainChunk(coord) : current.CloneUnfrozen();
            _chunks[coord.Key] = writable;
            return writable;
        }

        private static void Normalize(ref byte material, ref byte occupancy)
        {
            if (occupancy == 0 || material == TerrainMaterials.Air)
            {
                material = TerrainMaterials.Air;
                occupancy = 0;
            }
        }

        private void CollectRemesh()
        {
            _remeshKeys.Clear();
            for (int index = 0; index < _touched.Count; index++)
            {
                TouchedChunk touched = _touched[index];
                _changeSet.Changed.Add(touched.Coord);
                _remeshKeys.Add(touched.Coord.Key);
            }

            for (int index = 0; index < _touched.Count; index++)
            {
                TouchedChunk touched = _touched[index];
                if (touched.MinX == 0)
                {
                    AddNeighbour(touched.Coord.Offset(-1, 0, 0));
                }

                if (touched.MaxX == TerrainChunkCoord.Mask)
                {
                    AddNeighbour(touched.Coord.Offset(1, 0, 0));
                }

                if (touched.MinY == 0)
                {
                    AddNeighbour(touched.Coord.Offset(0, -1, 0));
                }

                if (touched.MaxY == TerrainChunkCoord.Mask)
                {
                    AddNeighbour(touched.Coord.Offset(0, 1, 0));
                }

                if (touched.MinZ == 0)
                {
                    AddNeighbour(touched.Coord.Offset(0, 0, -1));
                }

                if (touched.MaxZ == TerrainChunkCoord.Mask)
                {
                    AddNeighbour(touched.Coord.Offset(0, 0, 1));
                }
            }

            _changeSet.Remesh.AddRange(_changeSet.Changed);
            foreach (long key in _remeshKeys)
            {
                TerrainChunkCoord coord = TerrainChunkCoord.FromKey(key);
                if (!ContainsCoord(_changeSet.Changed, coord))
                {
                    _changeSet.Remesh.Add(coord);
                }
            }

            _changeSet.Remesh.Sort();
        }

        private void AddNeighbour(TerrainChunkCoord coord)
        {
            // WHY only allocated neighbours: an unallocated chunk is all Air and draws nothing, so no
            // face of it can depend on the changed cells.
            if (_chunks.ContainsKey(coord.Key))
            {
                _remeshKeys.Add(coord.Key);
            }
        }

        private static bool ContainsCoord(List<TerrainChunkCoord> list, TerrainChunkCoord coord)
        {
            for (int index = 0; index < list.Count; index++)
            {
                if (list[index] == coord)
                {
                    return true;
                }
            }

            return false;
        }

        private static void DeduplicateAndSort(List<TerrainChunkCoord> list)
        {
            list.Sort();
            int write = 0;
            for (int read = 0; read < list.Count; read++)
            {
                if (write == 0 || list[write - 1] != list[read])
                {
                    list[write++] = list[read];
                }
            }

            list.RemoveRange(write, list.Count - write);
        }

        private void Raise()
        {
            Action<TerrainChangeSet> handlers = Changed;
            if (handlers == null)
            {
                return;
            }

            _notifying = true;
            try
            {
                handlers(_changeSet);
            }
            finally
            {
                _notifying = false;
            }
        }

        private void ThrowIfNotifying()
        {
            if (_notifying)
            {
                throw new InvalidOperationException(
                    "A Terrain Changed handler tried to write to the Terrain it observes; defer the write.");
            }
        }

        private void RequireInBounds(in TerrainCellBox box, string operation)
        {
            if (!_limits.Contains(box))
            {
                throw RbxError.BadArgument(
                    operation + " reaches outside the Terrain bounds " + _limits.DescribeStuds() + " (" + box + ")",
                    "keep the region inside the Terrain bounds");
            }
        }

        private static void RequireSlot(byte slot)
        {
            if (!TerrainMaterials.IsValidSlot(slot))
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, "unknown Terrain material slot");
            }
        }

        private static void RequireBuffers(in TerrainCellBox box, byte[] materials, byte[] occupancy,
            string operation)
        {
            if (materials == null || occupancy == null)
            {
                throw new ArgumentNullException(materials == null ? nameof(materials) : nameof(occupancy));
            }

            if (box.CellCount > int.MaxValue || materials.Length < box.CellCount
                                             || occupancy.Length < box.CellCount)
            {
                throw new ArgumentException(operation + " buffers must hold " + box.CellCount + " cells.");
            }
        }

        private readonly struct TouchedChunk
        {
            public TouchedChunk(TerrainChunkCoord coord, int minX, int minY, int minZ, int maxX, int maxY,
                int maxZ)
            {
                Coord = coord;
                MinX = minX;
                MinY = minY;
                MinZ = minZ;
                MaxX = maxX;
                MaxY = maxY;
                MaxZ = maxZ;
            }

            public TerrainChunkCoord Coord { get; }

            public int MinX { get; }

            public int MinY { get; }

            public int MinZ { get; }

            public int MaxX { get; }

            public int MaxY { get; }

            public int MaxZ { get; }
        }

        // ---- Operations ----------------------------------------------------------------------

        private struct ConstantFill : ITerrainCellOperation
        {
            private readonly byte _material;
            private readonly byte _occupancy;

            public ConstantFill(byte material, byte occupancy)
            {
                _material = material;
                _occupancy = occupancy;
            }

            public bool OnlyClears => _material == TerrainMaterials.Air || _occupancy == 0;

            public bool Select(int x, int y, int z, byte material, byte occupancy, out byte newMaterial,
                out byte newOccupancy)
            {
                newMaterial = _material;
                newOccupancy = _occupancy;
                return true;
            }
        }

        private struct OrientedFill : ITerrainCellOperation
        {
            private readonly TerrainBoxRasterizer _rasterizer;
            private readonly byte _material;
            private readonly byte _occupancy;

            public OrientedFill(TerrainBoxRasterizer rasterizer, byte material, byte occupancy)
            {
                _rasterizer = rasterizer;
                _material = material;
                _occupancy = occupancy;
            }

            public bool OnlyClears => _material == TerrainMaterials.Air || _occupancy == 0;

            public bool Select(int x, int y, int z, byte material, byte occupancy, out byte newMaterial,
                out byte newOccupancy)
            {
                newMaterial = _material;
                newOccupancy = _occupancy;
                return _rasterizer.ContainsCellCentre(x, y, z);
            }
        }

        private struct BufferWrite : ITerrainCellOperation
        {
            private readonly TerrainCellBox _box;
            private readonly byte[] _materials;
            private readonly byte[] _occupancy;

            public BufferWrite(TerrainCellBox box, byte[] materials, byte[] occupancy)
            {
                _box = box;
                _materials = materials;
                _occupancy = occupancy;
            }

            public bool OnlyClears => false;

            public bool Select(int x, int y, int z, byte material, byte occupancy, out byte newMaterial,
                out byte newOccupancy)
            {
                int index = _box.RegionIndexOf(x, y, z);
                newMaterial = _materials[index];
                newOccupancy = _occupancy[index];
                return true;
            }
        }

        private struct ReplaceOperation : ITerrainCellOperation
        {
            private readonly byte _source;
            private readonly byte _target;

            public ReplaceOperation(byte source, byte target)
            {
                _source = source;
                _target = target;
            }

            public bool OnlyClears => _target == TerrainMaterials.Air;

            public bool Select(int x, int y, int z, byte material, byte occupancy, out byte newMaterial,
                out byte newOccupancy)
            {
                newMaterial = _target;
                newOccupancy = _source == TerrainMaterials.Air ? TerrainOccupancy.Full : occupancy;
                return material == _source;
            }
        }
    }

    /// <summary>One write rule the store applies to every cell of a box.</summary>
    internal interface ITerrainCellOperation
    {
        /// <summary>True when the operation can only empty cells, so it never needs a budget check.</summary>
        bool OnlyClears { get; }

        /// <summary>True with the new value when cell (x, y, z) is written; false leaves it as it is.</summary>
        bool Select(int x, int y, int z, byte material, byte occupancy, out byte newMaterial,
            out byte newOccupancy);
    }
}
