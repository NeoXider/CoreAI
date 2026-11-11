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
        private readonly List<int> _indices = new();
        private readonly int[] _submeshStart = new int[TerrainMaterials.SlotCount];
        private readonly int[] _submeshLength = new int[TerrainMaterials.SlotCount];

        /// <summary>
        /// Rebuilds <paramref name="mesh"/> from every non-Water slot of <paramref name="buffers"/>.
        /// Returns the faces written; <paramref name="slotMask"/> has bit <c>s</c> set for each slot
        /// that got a submesh, in the order the submeshes appear.
        /// </summary>
        public int BuildSolid(TerrainChunkMeshBuffers buffers, Mesh mesh, out int slotMask)
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
                AppendQuads(quads);
                _submeshLength[submeshes] = _indices.Count - _submeshStart[submeshes];
                slotMask |= 1 << slot;
                submeshes++;
            }

            Commit(mesh, submeshes);
            return _vertices.Count / 4;
        }

        /// <summary>Rebuilds <paramref name="mesh"/> from the Water faces of <paramref name="buffers"/>; returns the faces written.</summary>
        public int BuildWater(TerrainChunkMeshBuffers buffers, Mesh mesh)
        {
            Require(buffers, mesh);
            Begin();
            IReadOnlyList<TerrainFaceQuad> quads = buffers.GetQuads(TerrainMaterials.Water);
            int submeshes = 0;
            if (quads.Count > 0)
            {
                _submeshStart[0] = 0;
                AppendQuads(quads);
                _submeshLength[0] = _indices.Count;
                submeshes = 1;
            }

            Commit(mesh, submeshes);
            return _vertices.Count / 4;
        }

        private void Begin()
        {
            _vertices.Clear();
            _normals.Clear();
            _indices.Clear();
        }

        private void AppendQuads(IReadOnlyList<TerrainFaceQuad> quads)
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
                        (quad.CellY + dy) * (float)TerrainBoxRasterizer.CellStuds,
                        (quad.CellZ + dz) * (float)TerrainBoxRasterizer.CellStuds)));
                    _normals.Add(normal);
                }

                _indices.Add(first);
                _indices.Add(first + 2);
                _indices.Add(first + 1);
                _indices.Add(first);
                _indices.Add(first + 3);
                _indices.Add(first + 2);
            }
        }

        private void Commit(Mesh mesh, int submeshes)
        {
            mesh.Clear();
            mesh.indexFormat = _vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_vertices);
            mesh.SetNormals(_normals);
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
