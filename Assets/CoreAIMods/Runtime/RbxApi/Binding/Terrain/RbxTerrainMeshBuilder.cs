using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoreAI.Mods.Rbx.Binding.Terrain
{
    /// <summary>
    /// Turns the faces a <see cref="ITerrainChunkMesher"/> produced into Unity meshes: the solid
    /// faces as one mesh with a submesh per material slot (ascending slot order), the water faces as
    /// a mesh of their own. Positions go through <see cref="RbxSpace"/>, the one conversion boundary.
    /// Every vertex carries its sky exposure in the vertex colour's alpha (255 under the open sky; the
    /// colour's RGB stays white), which the terrain materials read to dim the sky's light in caves.
    /// </summary>
    /// <remarks>
    /// WHY world-space vertices on an identity transform: the material shaders project their
    /// patterns in object space, so with every chunk at the origin the patterns run on across chunk
    /// borders instead of restarting at each one. WHY the winding is reversed here: the Roblox-to-Unity
    /// Z mirror turns a counter-clockwise face clockwise, so emitting (0, 2, 1), (0, 3, 2) keeps every
    /// face front-facing outward. Buffers are reused, so a warm rebuild allocates nothing managed.
    /// </remarks>
    public sealed class RbxTerrainMeshBuilder
    {
        private readonly List<Vector3> _vertices = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Color32> _colors = new();
        private readonly byte[] _corners = new byte[4];
        private readonly List<int> _indices = new();
        private readonly int[] _submeshStart = new int[TerrainMaterials.SlotCount];
        private readonly int[] _submeshLength = new int[TerrainMaterials.SlotCount];

        /// <summary>
        /// Rebuilds <paramref name="mesh"/> from every non-Water slot of <paramref name="buffers"/>.
        /// Returns the faces written; <paramref name="slotMask"/> has bit <c>s</c> set for each slot
        /// that got a submesh, in the order the submeshes appear. A null <paramref name="exposure"/>
        /// writes every vertex as fully exposed; otherwise it must be prepared for the chunk.
        /// </summary>
        public int BuildSolid(TerrainChunkMeshBuffers buffers, Mesh mesh, out int slotMask,
            TerrainSkyExposure exposure = null)
        {
            Require(buffers, mesh);
            Begin();
            slotMask = 0;
            int submeshes = 0;
            for (int slot = 0; slot < TerrainMaterials.SlotCount; slot++)
            {
                if (slot == TerrainMaterials.Water || slot == TerrainMaterials.Air)
                {
                    continue;
                }

                IReadOnlyList<TerrainFaceQuad> quads = buffers.GetQuads((byte)slot);
                if (quads.Count == 0)
                {
                    continue;
                }

                _submeshStart[submeshes] = _indices.Count;
                AppendQuads(quads, exposure);
                _submeshLength[submeshes] = _indices.Count - _submeshStart[submeshes];
                slotMask |= 1 << slot;
                submeshes++;
            }

            Commit(mesh, submeshes);
            return _vertices.Count / 4;
        }

        /// <summary>
        /// Rebuilds <paramref name="mesh"/> from the Water faces of <paramref name="buffers"/>; returns the
        /// faces written. <paramref name="exposure"/> as in <see cref="BuildSolid"/>.
        /// </summary>
        public int BuildWater(TerrainChunkMeshBuffers buffers, Mesh mesh, TerrainSkyExposure exposure = null)
        {
            Require(buffers, mesh);
            Begin();
            IReadOnlyList<TerrainFaceQuad> quads = buffers.GetQuads(TerrainMaterials.Water);
            int submeshes = 0;
            if (quads.Count > 0)
            {
                _submeshStart[0] = 0;
                AppendQuads(quads, exposure);
                _submeshLength[0] = _indices.Count;
                submeshes = 1;
            }

            Commit(mesh, submeshes);
            return _vertices.Count / 4;
        }

        /// <summary>
        /// Rewrites only the sky exposure of a solid mesh <see cref="BuildSolid"/> built from the same faces
        /// (a write nearby changed the light but none of this chunk's faces). Returns false, touching
        /// nothing, when the mesh no longer has one vertex per face corner.
        /// </summary>
        public bool UpdateSolidExposure(TerrainChunkMeshBuffers buffers, Mesh mesh, TerrainSkyExposure exposure)
        {
            Require(buffers, mesh);
            _colors.Clear();
            for (int slot = 0; slot < TerrainMaterials.SlotCount; slot++)
            {
                if (slot != TerrainMaterials.Water && slot != TerrainMaterials.Air)
                {
                    AppendColors(buffers.GetQuads((byte)slot), exposure);
                }
            }

            return CommitColors(mesh);
        }

        /// <summary>The water counterpart of <see cref="UpdateSolidExposure"/>.</summary>
        public bool UpdateWaterExposure(TerrainChunkMeshBuffers buffers, Mesh mesh, TerrainSkyExposure exposure)
        {
            Require(buffers, mesh);
            _colors.Clear();
            AppendColors(buffers.GetQuads(TerrainMaterials.Water), exposure);
            return CommitColors(mesh);
        }

        private void Begin()
        {
            _vertices.Clear();
            _normals.Clear();
            _colors.Clear();
            _indices.Clear();
        }

        private void AppendQuads(IReadOnlyList<TerrainFaceQuad> quads, TerrainSkyExposure exposure)
        {
            for (int index = 0; index < quads.Count; index++)
            {
                TerrainFaceQuad quad = quads[index];
                TerrainFace face = quad.Face;
                Vector3 normal = RbxSpace.DirectionToUnity(TerrainFaceGeometry.GetNormal(face));
                int first = _vertices.Count;
                for (int corner = 0; corner < 4; corner++)
                {
                    TerrainFaceGeometry.GetCorner(face, corner, out int dx, out int dy, out int dz);
                    _vertices.Add(RbxSpace.ToUnity(new RbxVector3(
                        (quad.CellX + dx) * (float)TerrainBoxRasterizer.CellStuds,
                        (quad.CellY + quad.CornerHeight(dy)) * (float)TerrainBoxRasterizer.CellStuds,
                        (quad.CellZ + dz) * (float)TerrainBoxRasterizer.CellStuds)));
                    _normals.Add(normal);
                }

                AppendColors(in quad, exposure);
                _indices.Add(first);
                _indices.Add(first + 2);
                _indices.Add(first + 1);
                _indices.Add(first);
                _indices.Add(first + 3);
                _indices.Add(first + 2);
            }
        }

        private void AppendColors(IReadOnlyList<TerrainFaceQuad> quads, TerrainSkyExposure exposure)
        {
            for (int index = 0; index < quads.Count; index++)
            {
                TerrainFaceQuad quad = quads[index];
                AppendColors(in quad, exposure);
            }
        }

        private void AppendColors(in TerrainFaceQuad quad, TerrainSkyExposure exposure)
        {
            if (exposure == null)
            {
                Color32 open = new(255, 255, 255, TerrainSkyExposure.FullExposure);
                _colors.Add(open);
                _colors.Add(open);
                _colors.Add(open);
                _colors.Add(open);
                return;
            }

            exposure.GetCornerExposures(in quad, out byte corner0, out byte corner1, out byte corner2,
                out byte corner3);
            if (quad.Band != TerrainFaceBand.Whole)
            {
                _corners[0] = corner0;
                _corners[1] = corner1;
                _corners[2] = corner2;
                _corners[3] = corner3;
                corner0 = BandCorner(in quad, 0);
                corner1 = BandCorner(in quad, 1);
                corner2 = BandCorner(in quad, 2);
                corner3 = BandCorner(in quad, 3);
            }

            _colors.Add(new Color32(255, 255, 255, corner0));
            _colors.Add(new Color32(255, 255, 255, corner1));
            _colors.Add(new Color32(255, 255, 255, corner2));
            _colors.Add(new Color32(255, 255, 255, corner3));
        }

        /// <summary>The whole face's exposure along a corner's vertical edge, read at the height the band moved it to.</summary>
        private byte BandCorner(in TerrainFaceQuad quad, int corner)
        {
            TerrainFaceGeometry.GetCorner(quad.Face, corner, out int dx, out int dy, out int dz);
            int partner = corner;
            for (int other = 0; other < 4; other++)
            {
                TerrainFaceGeometry.GetCorner(quad.Face, other, out int ox, out int oy, out int oz);
                if (other != corner && ox == dx && oz == dz && oy != dy)
                {
                    partner = other;
                    break;
                }
            }

            float bottom = dy == 0 ? _corners[corner] : _corners[partner];
            float top = dy == 0 ? _corners[partner] : _corners[corner];
            return (byte)Mathf.RoundToInt(bottom + (top - bottom) * quad.CornerHeight(dy));
        }

        private bool CommitColors(Mesh mesh)
        {
            if (mesh.vertexCount != _colors.Count)
            {
                return false;
            }

            mesh.SetColors(_colors);
            return true;
        }

        private void Commit(Mesh mesh, int submeshes)
        {
            mesh.Clear();
            mesh.indexFormat = _vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_vertices);
            mesh.SetNormals(_normals);
            mesh.SetColors(_colors);
            mesh.subMeshCount = Math.Max(1, submeshes);
            for (int submesh = 0; submesh < submeshes; submesh++)
            {
                mesh.SetTriangles(_indices, _submeshStart[submesh], _submeshLength[submesh], submesh, false);
            }

            mesh.RecalculateBounds();
        }

        private static void Require(TerrainChunkMeshBuffers buffers, Mesh mesh)
        {
            if (buffers == null)
            {
                throw new ArgumentNullException(nameof(buffers));
            }

            if (mesh == null)
            {
                throw new ArgumentNullException(nameof(mesh));
            }
        }
    }
}
