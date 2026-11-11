using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// How much of the sky each Terrain cell sees, so a renderer can dim the sky's ambient light and
    /// reflections in a cave while the hill above it stays lit. Engine-free and deterministic: the same
    /// cells always give the same answer, whichever chunk asks.
    /// </summary>
    /// <remarks>
    /// The rule is the sky light of block games (Minecraft's sky light, Roblox's voxel lighting): a cell
    /// that blocks the sky is every solid cell (Water and Air do not); a non-blocking cell is <b>open</b>
    /// when no blocking cell lies anywhere above it in its column, and then has level
    /// <see cref="Reach"/>; any other non-blocking cell takes the highest level among its six neighbours
    /// minus one, so light flows in through cave mouths, shafts and from beside an overhang, around
    /// corners, and fades out <see cref="Reach"/> cells in. Exposure is (level / reach) squared: 1 under
    /// the open sky, about 0.7 one cell under an overhang, 0 deep in rock.
    /// <para>
    /// WHY a flood and not the share of open columns around a cell: a column test calls the foot of every
    /// hill and the base of every cliff half-covered, so the open world would darken next to any slope,
    /// and it cannot tell a cave mouth in a cliff face from solid rock. The flood keeps every cell under
    /// the open sky at exactly 1 and lets light enter sideways only where there is a way in.
    /// </para>
    /// <para>
    /// A level depends only on cells within <see cref="Reach"/> - 1 steps, so a chunk is lit from a
    /// window of its own cells plus that margin and the world column-top map; two chunks agree on every
    /// shared cell, and a write changes light only within that margin of the cells it changed and of the
    /// columns whose top it moved (<see cref="CollectLightAffectedChunks"/>).
    /// </para>
    /// Single-threaded, like the store; <see cref="OnStoreChanged"/> must run before anything is lit
    /// after a write.
    /// </remarks>
    public sealed class TerrainSkyExposure
    {
        /// <summary>Default <see cref="Reach"/>: light fades out six cells (24 studs) past an opening.</summary>
        public const int DefaultReach = 6;

        /// <summary>Largest accepted <see cref="Reach"/>; a window then still spans at most three chunks.</summary>
        public const int MaxReach = 15;

        /// <summary>The column top of a column that holds no blocking cell.</summary>
        public const int NoTop = int.MinValue;

        /// <summary>Exposure byte of a cell under the open sky.</summary>
        public const byte FullExposure = 255;

        private const byte Blocking = byte.MaxValue;
        private const int Edge = TerrainChunkCoord.Size;
        private const int ColumnCells = Edge * Edge;

        /// <summary>Changed cells up to which a write's light pass walks from them instead of scanning a box.</summary>
        private const int MaxWalkedWriteCells = 512;

        private const int CellKeyBias = 1 << 20;

        private readonly TerrainChunkStore _store;
        private readonly byte[] _exposureOfLevel;
        private readonly Dictionary<long, int[]> _columnTops = new();
        private readonly HashSet<long> _changedColumns = new();
        private readonly List<TopChange> _topChanges = new();
        private readonly HashSet<long> _affectedKeys = new();
        private readonly int[] _oldTops = new int[ColumnCells];
        private readonly HashSet<long> _walked = new();
        private readonly List<WalkStep> _walk = new();

        private byte[] _cells = Array.Empty<byte>();
        private int[] _tops = Array.Empty<int>();
        private int[] _queue = Array.Empty<int>();
        private readonly int[] _frontStep = new int[6];
        private readonly int[] _firstStep = new int[24];
        private readonly int[] _secondStep = new int[24];
        private bool _allOpen;
        private int _minX;
        private int _minY;
        private int _minZ;
        private int _sizeX;
        private int _sizeY;
        private int _sizeZ;
        private int _strideZ;
        private int _strideY;
        private bool _prepared;

        /// <summary>Lights the cells of <paramref name="store"/> with light that fades out <paramref name="reach"/> cells in.</summary>
        public TerrainSkyExposure(TerrainChunkStore store, int reach = DefaultReach)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            if (reach < 1 || reach > MaxReach)
            {
                throw new ArgumentOutOfRangeException(nameof(reach), reach, "reach must be 1.." + MaxReach);
            }

            Reach = reach;
            _exposureOfLevel = new byte[reach + 1];
            for (int level = 0; level <= reach; level++)
            {
                double share = (double)level / reach;
                _exposureOfLevel[level] = (byte)Math.Round(share * share * FullExposure);
            }
        }

        /// <summary>Cells light travels past an opening before it is gone; the level of an open cell.</summary>
        public int Reach { get; }

        /// <summary>True when a cell of <paramref name="material"/> keeps the sky from the cells below it.</summary>
        public static bool BlocksSky(byte material)
        {
            return material != TerrainMaterials.Air && material != TerrainMaterials.Water;
        }

        /// <summary>The exposure byte (0..255) of a non-blocking cell at <paramref name="level"/>.</summary>
        public byte ExposureOfLevel(int level)
        {
            return _exposureOfLevel[level <= 0 ? 0 : level >= Reach ? Reach : level];
        }

        /// <summary>The y of the highest cell of column (x, z) that blocks the sky; <see cref="NoTop"/> when none.</summary>
        public int GetColumnTop(int x, int z)
        {
            return Tops(x >> TerrainChunkCoord.Shift, z >> TerrainChunkCoord.Shift)[
                (x & TerrainChunkCoord.Mask) + ((z & TerrainChunkCoord.Mask) << TerrainChunkCoord.Shift)];
        }

        /// <summary>The light level (0..<see cref="Reach"/>) of cell (x, y, z); 0 for a blocking cell.</summary>
        public int SampleLevel(int x, int y, int z)
        {
            PrepareBox(x, y, z, 1, 1, 1);
            byte value = _cells[IndexOf(x, y, z)];
            return value == Blocking ? 0 : value;
        }

        /// <summary>
        /// Exposure of the space at cell (x, y, z) in 0..1, for something standing there (a part, a
        /// character): the cell's own exposure, or its brightest non-blocking neighbour's when the cell is solid.
        /// </summary>
        public float SampleExposure(int x, int y, int z)
        {
            PrepareBox(x - 1, y - 1, z - 1, 3, 3, 3);
            int index = IndexOf(x, y, z);
            byte value = _cells[index];
            if (value != Blocking)
            {
                return _exposureOfLevel[value] / (float)FullExposure;
            }

            int best = -1;
            best = Math.Max(best, LevelOrNone(index + 1));
            best = Math.Max(best, LevelOrNone(index - 1));
            best = Math.Max(best, LevelOrNone(index + _strideZ));
            best = Math.Max(best, LevelOrNone(index - _strideZ));
            best = Math.Max(best, LevelOrNone(index + _strideY));
            best = Math.Max(best, LevelOrNone(index - _strideY));
            return best < 0 ? 0f : _exposureOfLevel[best] / (float)FullExposure;
        }

        /// <summary>
        /// Lights chunk <paramref name="coord"/> and the cells around it, ready for
        /// <see cref="GetCornerExposures"/> on any face of that chunk. Reuses its buffers.
        /// </summary>
        public void PrepareChunk(TerrainChunkCoord coord)
        {
            PrepareBox(coord.MinCellX - 1, coord.MinCellY - 1, coord.MinCellZ - 1, Edge + 2, Edge + 2, Edge + 2);
        }

        /// <summary>
        /// The exposure (0..255) at the four corners of a face, in <see cref="TerrainFaceGeometry"/> corner
        /// order. Each corner is the mean over the non-blocking cells of the 2 x 2 cells in front of the face
        /// that touch it; the diagonal cell is left out when both cells beside it block, so light never leaks
        /// round a corner. The face's cell must lie in the chunk last prepared.
        /// </summary>
        public void GetCornerExposures(in TerrainFaceQuad quad, out byte corner0, out byte corner1, out byte corner2,
            out byte corner3)
        {
            if (!_prepared)
            {
                throw new InvalidOperationException("Prepare a chunk before reading its face corners.");
            }

            if (_allOpen)
            {
                // WHY: with no shaded cell around the chunk every non-blocking cell in front of a face is open,
                // so every corner is fully exposed; most surface chunks take this path.
                corner0 = FullExposure;
                corner1 = FullExposure;
                corner2 = FullExposure;
                corner3 = FullExposure;
                return;
            }

            int face = (int)quad.Face;
            int front = IndexOf(quad.CellX, quad.CellY, quad.CellZ) + _frontStep[face];
            corner0 = Corner(front, face * 4);
            corner1 = Corner(front, face * 4 + 1);
            corner2 = Corner(front, face * 4 + 2);
            corner3 = Corner(front, face * 4 + 3);
        }

        /// <summary>
        /// Brings the column-top map up to date after a write. Call from the store's
        /// <see cref="TerrainChunkStore.Changed"/> handler before anything is lit or collected.
        /// </summary>
        public void OnStoreChanged(TerrainChangeSet change)
        {
            if (change == null)
            {
                throw new ArgumentNullException(nameof(change));
            }

            _prepared = false;
            _topChanges.Clear();
            _changedColumns.Clear();
            IReadOnlyList<TerrainChunkCoord> changed = change.ChangedChunks;
            for (int index = 0; index < changed.Count; index++)
            {
                TerrainChunkCoord coord = changed[index];
                long columnKey = new TerrainChunkCoord(coord.X, 0, coord.Z).Key;
                if (!_changedColumns.Add(columnKey) || !_columnTops.TryGetValue(columnKey, out int[] tops))
                {
                    continue;
                }

                Array.Copy(tops, _oldTops, ColumnCells);
                ComputeTops(coord.X, coord.Z, tops);
                for (int cell = 0; cell < ColumnCells; cell++)
                {
                    if (tops[cell] != _oldTops[cell])
                    {
                        _topChanges.Add(new TopChange((coord.X << TerrainChunkCoord.Shift) + (cell & TerrainChunkCoord.Mask),
                            (coord.Z << TerrainChunkCoord.Shift) + (cell >> TerrainChunkCoord.Shift), _oldTops[cell],
                            tops[cell]));
                    }
                }
            }
        }

        /// <summary>
        /// Adds to <paramref name="into"/> every allocated chunk whose drawn light the last write may have
        /// changed although its faces did not (the write's <see cref="TerrainChangeSet.RemeshChunks"/> are
        /// left out, they are rebuilt anyway): chunks within <see cref="Reach"/> cells of a changed cell, and
        /// of the stretch of a column whose top moved, and only when that stretch holds a cell that is not
        /// open to the sky. Call after <see cref="OnStoreChanged"/> for the same change.
        /// </summary>
        public void CollectLightAffectedChunks(TerrainChangeSet change, List<TerrainChunkCoord> into)
        {
            if (change == null)
            {
                throw new ArgumentNullException(nameof(change));
            }

            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            _affectedKeys.Clear();
            IReadOnlyList<TerrainChunkCoord> remesh = change.RemeshChunks;
            for (int index = 0; index < remesh.Count; index++)
            {
                _affectedKeys.Add(remesh[index].Key);
            }

            // WHY Reach - 1: a level depends only on cells that many steps away, so no level outside this
            // margin of a changed cell can move.
            int margin = Reach - 1;
            TerrainCellBox cells = change.ChangedCells;
            if (!cells.IsEmpty)
            {
                // WHY both tests: a level near the write can move only if a shaded cell is there, and only if
                // some cell within reach of it sees the sky; a cavity sealed deep in rock is dark before and
                // after, so carving it costs no light pass.
                int far = 2 * margin;
                if (HasOpenCell(cells.MinX - far, cells.MinY - far, cells.MinZ - far, cells.MaxX + far,
                        cells.MaxY + far, cells.MaxZ + far)
                    && ReachesShadedCell(in cells, margin))
                {
                    AddChunksAround(cells.MinX - margin, cells.MinY - margin, cells.MinZ - margin, cells.MaxX + margin,
                        cells.MaxY + margin, cells.MaxZ + margin, into);
                }
            }
            else
            {
                IReadOnlyList<TerrainChunkCoord> changed = change.ChangedChunks;
                for (int index = 0; index < changed.Count; index++)
                {
                    TerrainChunkCoord coord = changed[index];
                    AddChunksAround(coord.MinCellX - margin, coord.MinCellY - margin, coord.MinCellZ - margin,
                        coord.MinCellX + Edge + margin, coord.MinCellY + Edge + margin, coord.MinCellZ + Edge + margin,
                        into);
                }
            }

            int floor = _store.Limits.MinCellY;
            for (int index = 0; index < _topChanges.Count; index++)
            {
                TopChange top = _topChanges[index];
                int low;
                int high;
                bool raised = top.Old == NoTop || (top.New != NoTop && top.New > top.Old);
                if (raised)
                {
                    // WHY below the new top only: the new top cell itself is the block just placed.
                    low = Math.Max(floor, top.Old == NoTop ? floor : top.Old + 1);
                    high = top.New - 1;
                }
                else
                {
                    low = Math.Max(floor, top.New == NoTop ? floor : top.New + 1);
                    high = top.Old;
                }

                if (high < low || !HasStretchThatChangedLight(top.X, top.Z, low, high, raised, cells))
                {
                    continue;
                }

                AddChunksAround(top.X - margin, low - margin, top.Z - margin, top.X + 1 + margin, high + 1 + margin,
                    top.Z + 1 + margin, into);
            }
        }

        /// <summary>
        /// True when a stretch of a column whose top moved holds a cell whose light changed with it: a
        /// non-blocking cell now kept from the sky (raised), or one that was kept from it and now sees it
        /// (lowered). On a lowered top a cell inside the changed box is skipped: it was the block removed,
        /// and its faces are rebuilt with the geometry.
        /// </summary>
        private bool HasStretchThatChangedLight(int x, int z, int low, int high, bool raised, in TerrainCellBox changed)
        {
            bool insideChangedColumn = !changed.IsEmpty && x >= changed.MinX && x < changed.MaxX && z >= changed.MinZ
                                       && z < changed.MaxZ;
            if (raised || !insideChangedColumn)
            {
                return HasNonBlockingCell(x, z, low, high);
            }

            if (low < changed.MinY && HasNonBlockingCell(x, z, low, Math.Min(high, changed.MinY - 1)))
            {
                return true;
            }

            return high >= changed.MaxY && HasNonBlockingCell(x, z, Math.Max(low, changed.MaxY), high);
        }

        private void AddChunksAround(int minX, int minY, int minZ, int maxX, int maxY, int maxZ,
            List<TerrainChunkCoord> into)
        {
            // WHY one more cell: a face reads the light of the cells in front of it, one cell outside its chunk.
            int chunkMinX = (minX - 1) >> TerrainChunkCoord.Shift;
            int chunkMinY = (minY - 1) >> TerrainChunkCoord.Shift;
            int chunkMinZ = (minZ - 1) >> TerrainChunkCoord.Shift;
            int chunkMaxX = maxX >> TerrainChunkCoord.Shift;
            int chunkMaxY = maxY >> TerrainChunkCoord.Shift;
            int chunkMaxZ = maxZ >> TerrainChunkCoord.Shift;
            for (int cy = chunkMinY; cy <= chunkMaxY; cy++)
            {
                for (int cz = chunkMinZ; cz <= chunkMaxZ; cz++)
                {
                    for (int cx = chunkMinX; cx <= chunkMaxX; cx++)
                    {
                        TerrainChunkCoord coord = new(cx, cy, cz);
                        if (_store.TryGetChunk(coord, out TerrainChunk _) && _affectedKeys.Add(coord.Key))
                        {
                            into.Add(coord);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// True when a shaded cell (non-blocking, a blocking cell somewhere above it) lies within
        /// <paramref name="margin"/> steps of the changed cells through non-blocking cells: only such a cell can
        /// change its level, since light reaches it along exactly such a path. A write too large to walk falls back
        /// to any shaded cell in the box grown by the margin.
        /// </summary>
        private bool ReachesShadedCell(in TerrainCellBox cells, int margin)
        {
            if (cells.CellCount > MaxWalkedWriteCells)
            {
                return HasShadedCell(cells.MinX - margin, cells.MinY - margin, cells.MinZ - margin, cells.MaxX + margin,
                    cells.MaxY + margin, cells.MaxZ + margin);
            }

            _walked.Clear();
            _walk.Clear();
            for (int y = cells.MinY - 1; y <= cells.MaxY; y++)
            {
                for (int z = cells.MinZ - 1; z <= cells.MaxZ; z++)
                {
                    for (int x = cells.MinX - 1; x <= cells.MaxX; x++)
                    {
                        Visit(x, y, z, 0);
                    }
                }
            }

            for (int head = 0; head < _walk.Count; head++)
            {
                WalkStep step = _walk[head];
                if (step.Y <= GetColumnTop(step.X, step.Z))
                {
                    return true;
                }

                if (step.Depth >= margin)
                {
                    continue;
                }

                int depth = step.Depth + 1;
                Visit(step.X + 1, step.Y, step.Z, depth);
                Visit(step.X - 1, step.Y, step.Z, depth);
                Visit(step.X, step.Y + 1, step.Z, depth);
                Visit(step.X, step.Y - 1, step.Z, depth);
                Visit(step.X, step.Y, step.Z + 1, depth);
                Visit(step.X, step.Y, step.Z - 1, depth);
            }

            return false;
        }

        private void Visit(int x, int y, int z, int depth)
        {
            long key = ((long)(x + CellKeyBias) << 42) | ((long)(y + CellKeyBias) << 21) | (long)(z + CellKeyBias);
            if (BlocksSky(_store.GetMaterial(x, y, z)) || !_walked.Add(key))
            {
                return;
            }

            _walk.Add(new WalkStep(x, y, z, depth));
        }

        /// <summary>True when the box holds a cell under the open sky.</summary>
        private bool HasOpenCell(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            for (int z = minZ; z < maxZ; z++)
            {
                for (int x = minX; x < maxX; x++)
                {
                    int top = GetColumnTop(x, z);
                    if (top == NoTop || Math.Max(top + 1, minY) < maxY)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>True when the box holds a non-blocking cell some blocking cell above keeps from the sky.</summary>
        private bool HasShadedCell(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            for (int z = minZ; z < maxZ; z++)
            {
                for (int x = minX; x < maxX; x++)
                {
                    int top = GetColumnTop(x, z);
                    if (top == NoTop || top < minY)
                    {
                        continue;
                    }

                    if (HasNonBlockingCell(x, z, minY, Math.Min(top, maxY - 1)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool HasNonBlockingCell(int x, int z, int fromY, int toY)
        {
            int y = fromY;
            while (y <= toY)
            {
                TerrainChunkCoord coord = TerrainChunkCoord.FromCell(x, y, z);
                int chunkTop = Math.Min(toY, coord.MinCellY + TerrainChunkCoord.Mask);
                if (!_store.TryGetChunk(coord, out TerrainChunk chunk))
                {
                    return true;
                }

                byte[] materials = chunk.MaterialArray;
                int lx = x & TerrainChunkCoord.Mask;
                int lz = z & TerrainChunkCoord.Mask;
                for (; y <= chunkTop; y++)
                {
                    if (!BlocksSky(materials[TerrainChunk.IndexOf(lx, y & TerrainChunkCoord.Mask, lz)]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private byte Corner(int front, int slot)
        {
            int firstStep = _firstStep[slot];
            int secondStep = _secondStep[slot];
            byte frontValue = _cells[front];
            byte sideA = _cells[front + firstStep];
            byte sideB = _cells[front + secondStep];
            int sum = frontValue == Blocking ? 0 : _exposureOfLevel[frontValue];
            int count = frontValue == Blocking ? 0 : 1;
            if (sideA != Blocking)
            {
                sum += _exposureOfLevel[sideA];
                count++;
            }

            if (sideB != Blocking)
            {
                sum += _exposureOfLevel[sideB];
                count++;
            }

            if (sideA != Blocking || sideB != Blocking)
            {
                byte diagonal = _cells[front + firstStep + secondStep];
                if (diagonal != Blocking)
                {
                    sum += _exposureOfLevel[diagonal];
                    count++;
                }
            }

            return count == 0 ? (byte)0 : (byte)((sum + count / 2) / count);
        }

        /// <summary>
        /// The window offsets a face corner reads: the front cell one step along the face normal, then one step
        /// along each of the face's two axes, towards the corner (the 2 x 2 cells in front that touch it).
        /// </summary>
        private void BuildCornerSteps()
        {
            for (int face = 0; face < 6; face++)
            {
                TerrainFaceGeometry.GetNormal((TerrainFace)face, out int nx, out int ny, out int nz);
                _frontStep[face] = nx + nz * _strideZ + ny * _strideY;
                for (int corner = 0; corner < 4; corner++)
                {
                    TerrainFaceGeometry.GetCorner((TerrainFace)face, corner, out int dx, out int dy, out int dz);
                    int first;
                    int second;
                    if (nx != 0)
                    {
                        first = (dy == 1 ? 1 : -1) * _strideY;
                        second = (dz == 1 ? 1 : -1) * _strideZ;
                    }
                    else if (ny != 0)
                    {
                        first = dx == 1 ? 1 : -1;
                        second = (dz == 1 ? 1 : -1) * _strideZ;
                    }
                    else
                    {
                        first = dx == 1 ? 1 : -1;
                        second = (dy == 1 ? 1 : -1) * _strideY;
                    }

                    _firstStep[face * 4 + corner] = first;
                    _secondStep[face * 4 + corner] = second;
                }
            }
        }

        private int LevelOrNone(int index)
        {
            byte value = _cells[index];
            return value == Blocking ? -1 : value;
        }

        private int IndexOf(int x, int y, int z)
        {
            return x - _minX + (z - _minZ) * _strideZ + (y - _minY) * _strideY;
        }

        /// <summary>
        /// Fills the light level of every cell of a needed box: a window of the box plus Reach - 1 cells on
        /// every side (every cell a level of the box can depend on), and one more cell of padding marked
        /// blocking so the flood never tests bounds.
        /// </summary>
        private void PrepareBox(int needMinX, int needMinY, int needMinZ, int needSizeX, int needSizeY, int needSizeZ)
        {
            int margin = Reach - 1 + 1;
            _minX = needMinX - margin;
            _minY = needMinY - margin;
            _minZ = needMinZ - margin;
            _sizeX = needSizeX + 2 * margin;
            _sizeY = needSizeY + 2 * margin;
            _sizeZ = needSizeZ + 2 * margin;
            _strideZ = _sizeX;
            _strideY = _sizeX * _sizeZ;
            int cellCount = _strideY * _sizeY;
            if (_cells.Length < cellCount)
            {
                _cells = new byte[cellCount];
                _queue = new int[cellCount];
            }

            if (_tops.Length < _sizeX * _sizeZ)
            {
                _tops = new int[_sizeX * _sizeZ];
            }

            for (int wz = 0; wz < _sizeZ; wz++)
            {
                int z = _minZ + wz;
                int wx = 0;
                while (wx < _sizeX)
                {
                    int x = _minX + wx;
                    int[] columnTops = Tops(x >> TerrainChunkCoord.Shift, z >> TerrainChunkCoord.Shift);
                    int rowOffset = (z & TerrainChunkCoord.Mask) << TerrainChunkCoord.Shift;
                    int run = Math.Min(_sizeX - wx, Edge - (x & TerrainChunkCoord.Mask));
                    Array.Copy(columnTops, rowOffset + (x & TerrainChunkCoord.Mask), _tops, wx + wz * _sizeX, run);
                    wx += run;
                }
            }

            BuildCornerSteps();
            Classify(margin, margin, margin, margin + needSizeX, margin + needSizeY, margin + needSizeZ);
            _prepared = true;
            _allOpen = !HasShadedCellInWindow(needMinX, needMinY, needMinZ, needSizeX, needSizeY, needSizeZ);
            if (_allOpen || !WindowHasOpenCell())
            {
                // WHY no flood without an open cell: light only enters from open cells, so a window deep in rock
                // keeps every shaded cell at 0 exactly as the needed box already holds it.
                return;
            }

            Classify(0, 0, 0, _sizeX, _sizeY, _sizeZ);
            MarkPadding();
            Flood();
        }

        /// <summary>True when some column of the window is open below the window's top row.</summary>
        private bool WindowHasOpenCell()
        {
            int topRow = _minY + _sizeY - 2;
            for (int column = 0; column < _sizeX * _sizeZ; column++)
            {
                if (_tops[column] < topRow)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Writes the starting value of every cell of a window sub-box (window coordinates, max exclusive):
        /// <see cref="Reach"/> when open, 0 when shaded, <see cref="Blocking"/> when solid.
        /// </summary>
        private void Classify(int fromX, int fromY, int fromZ, int toX, int toY, int toZ)
        {
            byte open = (byte)Reach;
            for (int wy = fromY; wy < toY; wy++)
            {
                int y = _minY + wy;
                for (int wz = fromZ; wz < toZ; wz++)
                {
                    int row = wy * _strideY + wz * _strideZ;
                    int columns = wz * _sizeX;
                    for (int wx = fromX; wx < toX; wx++)
                    {
                        _cells[row + wx] = y > _tops[columns + wx] ? open : (byte)0;
                    }
                }
            }

            MarkBlockingCells(_minX + fromX, _minY + fromY, _minZ + fromZ, _minX + toX, _minY + toY, _minZ + toZ);
        }

        private void MarkBlockingCells(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            int chunkMinX = minX >> TerrainChunkCoord.Shift;
            int chunkMinY = minY >> TerrainChunkCoord.Shift;
            int chunkMinZ = minZ >> TerrainChunkCoord.Shift;
            int chunkMaxX = (maxX - 1) >> TerrainChunkCoord.Shift;
            int chunkMaxY = (maxY - 1) >> TerrainChunkCoord.Shift;
            int chunkMaxZ = (maxZ - 1) >> TerrainChunkCoord.Shift;
            for (int cy = chunkMinY; cy <= chunkMaxY; cy++)
            {
                for (int cz = chunkMinZ; cz <= chunkMaxZ; cz++)
                {
                    for (int cx = chunkMinX; cx <= chunkMaxX; cx++)
                    {
                        TerrainChunkCoord coord = new(cx, cy, cz);
                        if (!_store.TryGetChunk(coord, out TerrainChunk chunk))
                        {
                            continue;
                        }

                        byte[] materials = chunk.MaterialArray;
                        int x0 = Math.Max(minX, coord.MinCellX);
                        int x1 = Math.Min(maxX, coord.MinCellX + Edge);
                        int y0 = Math.Max(minY, coord.MinCellY);
                        int y1 = Math.Min(maxY, coord.MinCellY + Edge);
                        int z0 = Math.Max(minZ, coord.MinCellZ);
                        int z1 = Math.Min(maxZ, coord.MinCellZ + Edge);
                        for (int y = y0; y < y1; y++)
                        {
                            for (int z = z0; z < z1; z++)
                            {
                                int source = TerrainChunk.IndexOf(x0 - coord.MinCellX, y - coord.MinCellY,
                                    z - coord.MinCellZ);
                                int target = IndexOf(x0, y, z);
                                for (int x = x0; x < x1; x++, source++, target++)
                                {
                                    if (BlocksSky(materials[source]))
                                    {
                                        _cells[target] = Blocking;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private void MarkPadding()
        {
            for (int wy = 0; wy < _sizeY; wy++)
            {
                for (int wz = 0; wz < _sizeZ; wz++)
                {
                    bool edgeRow = wy == 0 || wy == _sizeY - 1 || wz == 0 || wz == _sizeZ - 1;
                    int row = wy * _strideY + wz * _strideZ;
                    if (edgeRow)
                    {
                        for (int wx = 0; wx < _sizeX; wx++)
                        {
                            _cells[row + wx] = Blocking;
                        }
                    }
                    else
                    {
                        _cells[row] = Blocking;
                        _cells[row + _sizeX - 1] = Blocking;
                    }
                }
            }
        }

        private bool HasShadedCellInWindow(int needMinX, int needMinY, int needMinZ, int needSizeX, int needSizeY,
            int needSizeZ)
        {
            for (int y = needMinY; y < needMinY + needSizeY; y++)
            {
                for (int z = needMinZ; z < needMinZ + needSizeZ; z++)
                {
                    int index = IndexOf(needMinX, y, z);
                    for (int x = 0; x < needSizeX; x++, index++)
                    {
                        if (_cells[index] == 0)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private void Flood()
        {
            byte open = (byte)Reach;
            byte seedLevel = (byte)(Reach - 1);
            int head = 0;
            int tail = 0;
            if (seedLevel == 0)
            {
                return;
            }

            int count = _strideY * _sizeY;
            for (int index = _strideY; index < count - _strideY; index++)
            {
                if (_cells[index] != 0)
                {
                    continue;
                }

                if (_cells[index + 1] == open || _cells[index - 1] == open || _cells[index + _strideZ] == open
                    || _cells[index - _strideZ] == open || _cells[index + _strideY] == open
                    || _cells[index - _strideY] == open)
                {
                    _cells[index] = seedLevel;
                    _queue[tail++] = index;
                }
            }

            while (head < tail)
            {
                int index = _queue[head++];
                int next = _cells[index] - 1;
                if (next <= 0)
                {
                    continue;
                }

                byte level = (byte)next;
                tail = Spread(index + 1, level, tail);
                tail = Spread(index - 1, level, tail);
                tail = Spread(index + _strideZ, level, tail);
                tail = Spread(index - _strideZ, level, tail);
                tail = Spread(index + _strideY, level, tail);
                tail = Spread(index - _strideY, level, tail);
            }
        }

        private int Spread(int index, byte level, int tail)
        {
            if (_cells[index] != 0)
            {
                return tail;
            }

            _cells[index] = level;
            _queue[tail] = index;
            return tail + 1;
        }

        private int[] Tops(int chunkX, int chunkZ)
        {
            long key = new TerrainChunkCoord(chunkX, 0, chunkZ).Key;
            if (_columnTops.TryGetValue(key, out int[] tops))
            {
                return tops;
            }

            tops = new int[ColumnCells];
            ComputeTops(chunkX, chunkZ, tops);
            _columnTops.Add(key, tops);
            return tops;
        }

        private void ComputeTops(int chunkX, int chunkZ, int[] tops)
        {
            for (int cell = 0; cell < ColumnCells; cell++)
            {
                tops[cell] = NoTop;
            }

            int remaining = ColumnCells;
            int topChunkY = (_store.Limits.MaxCellY - 1) >> TerrainChunkCoord.Shift;
            int bottomChunkY = _store.Limits.MinCellY >> TerrainChunkCoord.Shift;
            for (int cy = topChunkY; cy >= bottomChunkY && remaining > 0; cy--)
            {
                if (!_store.TryGetChunk(new TerrainChunkCoord(chunkX, cy, chunkZ), out TerrainChunk chunk))
                {
                    continue;
                }

                byte[] materials = chunk.MaterialArray;
                for (int cell = 0; cell < ColumnCells; cell++)
                {
                    if (tops[cell] != NoTop)
                    {
                        continue;
                    }

                    for (int ly = TerrainChunkCoord.Mask; ly >= 0; ly--)
                    {
                        if (BlocksSky(materials[cell + (ly << (2 * TerrainChunkCoord.Shift))]))
                        {
                            tops[cell] = (cy << TerrainChunkCoord.Shift) + ly;
                            remaining--;
                            break;
                        }
                    }
                }
            }
        }

        private readonly struct WalkStep
        {
            public WalkStep(int x, int y, int z, int depth)
            {
                X = x;
                Y = y;
                Z = z;
                Depth = depth;
            }

            public int X { get; }

            public int Y { get; }

            public int Z { get; }

            public int Depth { get; }
        }

        private readonly struct TopChange
        {
            public TopChange(int x, int z, int old, int next)
            {
                X = x;
                Z = z;
                Old = old;
                New = next;
            }

            public int X { get; }

            public int Z { get; }

            public int Old { get; }

            public int New { get; }
        }
    }
}
