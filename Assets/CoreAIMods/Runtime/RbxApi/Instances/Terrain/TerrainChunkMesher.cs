using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>One of the six axis-aligned faces of a cell.</summary>
    public enum TerrainFace : byte
    {
        PositiveX = 0,
        NegativeX = 1,
        PositiveY = 2,
        NegativeY = 3,
        PositiveZ = 4,
        NegativeZ = 5
    }

    /// <summary>Which part of a side face a quad covers when a face rule splits it (<see cref="TerrainFaceMaterialRule"/>).</summary>
    public enum TerrainFaceBand : byte
    {
        /// <summary>The whole face.</summary>
        Whole = 0,

        /// <summary>The band along the face's upper edge.</summary>
        Fringe = 1,

        /// <summary>The face below the band.</summary>
        BelowFringe = 2
    }

    /// <summary>One visible face of one cell (world cell coordinates), or one band of a split side face.</summary>
    public readonly struct TerrainFaceQuad
    {
        public TerrainFaceQuad(int cellX, int cellY, int cellZ, TerrainFace face)
            : this(cellX, cellY, cellZ, face, TerrainFaceBand.Whole, 0)
        {
        }

        /// <summary>A band of a side face split <paramref name="fringeSixteenths"/> sixteenths below its upper edge.</summary>
        public TerrainFaceQuad(int cellX, int cellY, int cellZ, TerrainFace face, TerrainFaceBand band,
            byte fringeSixteenths)
        {
            CellX = cellX;
            CellY = cellY;
            CellZ = cellZ;
            Face = face;
            Band = band;
            FringeSixteenths = fringeSixteenths;
        }

        public int CellX { get; }

        public int CellY { get; }

        public int CellZ { get; }

        public TerrainFace Face { get; }

        /// <summary>The part of the face this quad covers.</summary>
        public TerrainFaceBand Band { get; }

        /// <summary>Where the face is split, in sixteenths of a cell below its upper edge; 0 for a whole face.</summary>
        public byte FringeSixteenths { get; }

        /// <summary>
        /// Height (0..1 of the cell) of a face corner that sits at <paramref name="cornerDy"/> (0 bottom, 1 top)
        /// once the band is cut: a fringe starts at the split, the face below it ends there.
        /// </summary>
        public float CornerHeight(int cornerDy)
        {
            float split = 1f - FringeSixteenths / 16f;
            if (Band == TerrainFaceBand.Fringe && cornerDy == 0)
            {
                return split;
            }

            if (Band == TerrainFaceBand.BelowFringe && cornerDy == 1)
            {
                return split;
            }

            return cornerDy;
        }
    }

    /// <summary>
    /// The geometry of a face in Roblox space: corner offsets in cell units from the cell's minimum
    /// corner, listed counter-clockwise as seen from outside the cell (right-handed), and the outward
    /// normal. Triangles are corners (0, 1, 2) and (0, 2, 3).
    /// </summary>
    public static class TerrainFaceGeometry
    {
        private static readonly sbyte[] Corners =
        {
            1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1,
            0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 0,
            0, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0,
            0, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1,
            0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1,
            0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0
        };

        private static readonly sbyte[] Normals =
        {
            1, 0, 0, -1, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 1, 0, 0, -1
        };

        /// <summary>Corner <paramref name="corner"/> (0..3) of <paramref name="face"/>, in cell units.</summary>
        public static void GetCorner(TerrainFace face, int corner, out int dx, out int dy, out int dz)
        {
            int offset = (int)face * 12 + corner * 3;
            dx = Corners[offset];
            dy = Corners[offset + 1];
            dz = Corners[offset + 2];
        }

        /// <summary>The outward unit normal of <paramref name="face"/>.</summary>
        public static void GetNormal(TerrainFace face, out int nx, out int ny, out int nz)
        {
            int offset = (int)face * 3;
            nx = Normals[offset];
            ny = Normals[offset + 1];
            nz = Normals[offset + 2];
        }

        /// <summary>The outward unit normal of <paramref name="face"/> as a Vector3.</summary>
        public static RbxVector3 GetNormal(TerrainFace face)
        {
            GetNormal(face, out int nx, out int ny, out int nz);
            return new RbxVector3(nx, ny, nz);
        }
    }

    /// <summary>
    /// What a mesher produced for one chunk: the visible faces grouped by the material slot they are drawn
    /// with (a face rule may draw a cell's face with another material than the cell's own). Reused
    /// across chunks; <see cref="Clear"/> keeps the list capacity, so re-meshing allocates nothing
    /// once the lists have grown.
    /// </summary>
    public sealed class TerrainChunkMeshBuffers
    {
        private readonly List<TerrainFaceQuad>[] _bySlot;

        public TerrainChunkMeshBuffers()
        {
            _bySlot = new List<TerrainFaceQuad>[TerrainMaterials.SlotCount];
            for (int slot = 0; slot < _bySlot.Length; slot++)
            {
                _bySlot[slot] = new List<TerrainFaceQuad>();
            }
        }

        /// <summary>Faces of every slot, water included.</summary>
        public int QuadCount { get; private set; }

        /// <summary>The faces of one material slot (<see cref="TerrainMaterials.Water"/> is the liquid surface).</summary>
        public IReadOnlyList<TerrainFaceQuad> GetQuads(byte slot)
        {
            return _bySlot[slot];
        }

        /// <summary>Appends one face of <paramref name="slot"/>.</summary>
        public void Add(byte slot, in TerrainFaceQuad quad)
        {
            _bySlot[slot].Add(quad);
            QuadCount++;
        }

        /// <summary>Empties every slot, keeping capacity.</summary>
        public void Clear()
        {
            for (int slot = 0; slot < _bySlot.Length; slot++)
            {
                _bySlot[slot].Clear();
            }

            QuadCount = 0;
        }
    }

    /// <summary>
    /// The mesher port: turns the cells of one chunk into faces. The default is
    /// <see cref="BlockyTerrainChunkMesher"/>; a host replaces it (smooth or greedy meshing) without
    /// touching the store or the renderer.
    /// </summary>
    public interface ITerrainChunkMesher
    {
        /// <summary>Fills <paramref name="output"/> (cleared first) with the faces of chunk <paramref name="coord"/>.</summary>
        void BuildChunk(TerrainChunkStore store, TerrainChunkCoord coord, TerrainChunkMeshBuffers output);
    }

    /// <summary>
    /// Face-culled cubes: every non-Air cell draws as a full cube, and a face is emitted only where
    /// it can be seen. A solid face shows against Air or Water; a Water face shows only against Air,
    /// so touching water cells and water against a solid draw no face between them. Each face is drawn
    /// with the material its cell's <see cref="TerrainFaceMaterialRule"/> names for that side; a side face
    /// whose rule has a fringe, of a cell open above, is split into the fringe and the face below it.
    /// </summary>
    /// <remarks>
    /// WHY no greedy merging: a 16-cell chunk has at most 12,288 faces (a checkerboard), 20,480 quads with
    /// every side of a grass checkerboard split by its band, and merging pays off only once measurement says
    /// so; the mesh builder switches to a 32-bit index buffer past 65,535 vertices.
    /// </remarks>
    public sealed class BlockyTerrainChunkMesher : ITerrainChunkMesher
    {
        private const int Edge = TerrainChunkCoord.Size;
        private const int Last = TerrainChunkCoord.Mask;
        private const int ZStride = Edge;
        private const int YStride = Edge * Edge;

        private readonly TerrainFaceMaterialRules _rules;

        /// <summary>A mesher drawing faces by <paramref name="rules"/>; null uses <see cref="TerrainFaceMaterialRules.Default"/>.</summary>
        public BlockyTerrainChunkMesher(TerrainFaceMaterialRules rules = null)
        {
            _rules = rules ?? TerrainFaceMaterialRules.Default;
        }

        /// <summary>The face rules this mesher draws with.</summary>
        public TerrainFaceMaterialRules Rules => _rules;

        /// <inheritdoc />
        public void BuildChunk(TerrainChunkStore store, TerrainChunkCoord coord, TerrainChunkMeshBuffers output)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            output.Clear();
            if (!store.TryGetChunk(coord, out TerrainChunk chunk))
            {
                return;
            }

            byte[] materials = chunk.MaterialArray;
            byte[] positiveX = NeighbourMaterials(store, coord.Offset(1, 0, 0));
            byte[] negativeX = NeighbourMaterials(store, coord.Offset(-1, 0, 0));
            byte[] positiveY = NeighbourMaterials(store, coord.Offset(0, 1, 0));
            byte[] negativeY = NeighbourMaterials(store, coord.Offset(0, -1, 0));
            byte[] positiveZ = NeighbourMaterials(store, coord.Offset(0, 0, 1));
            byte[] negativeZ = NeighbourMaterials(store, coord.Offset(0, 0, -1));
            int originX = coord.MinCellX;
            int originY = coord.MinCellY;
            int originZ = coord.MinCellZ;
            for (int ly = 0; ly < Edge; ly++)
            {
                for (int lz = 0; lz < Edge; lz++)
                {
                    int index = TerrainChunk.IndexOf(0, ly, lz);
                    for (int lx = 0; lx < Edge; lx++, index++)
                    {
                        byte material = materials[index];
                        if (material == TerrainMaterials.Air)
                        {
                            continue;
                        }

                        int x = originX + lx;
                        int y = originY + ly;
                        int z = originZ + lz;
                        TerrainFaceMaterialRule rule = _rules.For(material);
                        byte above = ly < Last ? materials[index + YStride] : Read(positiveY, index - Last * YStride);
                        // WHY only under open space: a block laid on a grass block hides its top, and its sides
                        // show earth to the edge.
                        byte fringe = above == TerrainMaterials.Air || above == TerrainMaterials.Water
                            ? rule.FringeSixteenths
                            : (byte)0;
                        byte next = lx < Last ? materials[index + 1] : Read(positiveX, index - Last);
                        EmitSide(output, material, next, x, y, z, TerrainFace.PositiveX, in rule, fringe);
                        next = lx > 0 ? materials[index - 1] : Read(negativeX, index + Last);
                        EmitSide(output, material, next, x, y, z, TerrainFace.NegativeX, in rule, fringe);
                        Emit(output, material, rule.Top, above, x, y, z, TerrainFace.PositiveY);
                        next = ly > 0 ? materials[index - YStride] : Read(negativeY, index + Last * YStride);
                        Emit(output, material, rule.Bottom, next, x, y, z, TerrainFace.NegativeY);
                        next = lz < Last ? materials[index + ZStride] : Read(positiveZ, index - Last * ZStride);
                        EmitSide(output, material, next, x, y, z, TerrainFace.PositiveZ, in rule, fringe);
                        next = lz > 0 ? materials[index - ZStride] : Read(negativeZ, index + Last * ZStride);
                        EmitSide(output, material, next, x, y, z, TerrainFace.NegativeZ, in rule, fringe);
                    }
                }
            }
        }

        /// <summary>True when a face of <paramref name="material"/> against <paramref name="neighbour"/> can be seen.</summary>
        public static bool IsFaceVisible(byte material, byte neighbour)
        {
            if (material == TerrainMaterials.Air)
            {
                return false;
            }

            if (material == TerrainMaterials.Water)
            {
                return neighbour == TerrainMaterials.Air;
            }

            return neighbour == TerrainMaterials.Air || neighbour == TerrainMaterials.Water;
        }

        private static void Emit(TerrainChunkMeshBuffers output, byte material, byte drawn, byte neighbour, int x,
            int y, int z, TerrainFace face)
        {
            if (IsFaceVisible(material, neighbour))
            {
                output.Add(drawn, new TerrainFaceQuad(x, y, z, face));
            }
        }

        private static void EmitSide(TerrainChunkMeshBuffers output, byte material, byte neighbour, int x, int y,
            int z, TerrainFace face, in TerrainFaceMaterialRule rule, byte fringe)
        {
            if (!IsFaceVisible(material, neighbour))
            {
                return;
            }

            if (fringe == 0 || rule.Side == rule.Top)
            {
                output.Add(rule.Side, new TerrainFaceQuad(x, y, z, face));
                return;
            }

            output.Add(rule.Top, new TerrainFaceQuad(x, y, z, face, TerrainFaceBand.Fringe, fringe));
            output.Add(rule.Side, new TerrainFaceQuad(x, y, z, face, TerrainFaceBand.BelowFringe, fringe));
        }

        private static byte[] NeighbourMaterials(TerrainChunkStore store, TerrainChunkCoord coord)
        {
            return store.TryGetChunk(coord, out TerrainChunk chunk) ? chunk.MaterialArray : null;
        }

        private static byte Read(byte[] materials, int index)
        {
            return materials == null ? TerrainMaterials.Air : materials[index];
        }
    }
}
