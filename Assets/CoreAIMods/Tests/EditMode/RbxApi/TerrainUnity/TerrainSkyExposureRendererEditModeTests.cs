using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.TerrainUnity
{
    /// <summary>
    /// Unity EditMode: the sky exposure on the Terrain's meshes and materials. A chunk mesh carries the
    /// exposure in its vertex colours' alpha, a world without caves meshes exactly as before apart from that
    /// channel, the chunk materials are copies that read it while a Part's material does not, a write that
    /// opens a shaft relights the chunk under it within the same pass without re-meshing it, queued colliders
    /// can be built out of turn, and a part renderer in a cave is given its exposure.
    /// </summary>
    [TestFixture]
    public sealed class TerrainSkyExposureRendererEditModeTests
    {
        private readonly List<Object> _owned = new();
        private RbxTerrain _terrain;
        private RbxTerrainRenderer _renderer;
        private GameObject _host;

        [SetUp]
        public void CreateWorld()
        {
            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            if (!catalog.TryGet(RbxTerrain.ClassNameValue, out ClassDescriptor _))
            {
                catalog.Register(RbxTerrain.CreateDescriptor());
            }

            InstanceRegistry registry = new(catalog);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _terrain = RbxTerrainBootstrap.EnsureTerrain(registry, game.FindFirstChildOfClass("Workspace"),
                TerrainLimits.Desktop);
            _host = new GameObject("Sky exposure host");
            _owned.Add(_host);
            _renderer = new RbxTerrainRenderer(_terrain, _host.transform, new PlainMaterials(_owned));
        }

        [TearDown]
        public void DestroyWorld()
        {
            _renderer?.Dispose();
            foreach (Object owned in _owned)
            {
                if (owned != null)
                {
                    Object.DestroyImmediate(owned);
                }
            }

            _owned.Clear();
        }

        private static byte Slot(string name)
        {
            TerrainMaterials.TryGetSlot(name, out byte slot);
            return slot;
        }

        private void Fill(int x, int y, int z, int sizeX, int sizeY, int sizeZ, byte material)
        {
            _terrain.Store.FillBox(new TerrainCellBox(x, y, z, sizeX, sizeY, sizeZ), material,
                material == TerrainMaterials.Air ? (byte)0 : TerrainOccupancy.Full);
        }

        private void Drain()
        {
            while (_renderer.PendingChunkCount > 0)
            {
                _renderer.ProcessDirty(int.MaxValue, double.MaxValue);
            }
        }

        private static List<string> QuadCorners(int[] indices)
        {
            List<string> quads = new();
            for (int start = 0; start + 6 <= indices.Length; start += 6)
            {
                SortedSet<int> corners = new();
                for (int offset = 0; offset < 6; offset++)
                {
                    corners.Add(indices[start + offset]);
                }

                quads.Add(string.Join(",", corners));
            }

            return quads;
        }

        private Mesh SolidMesh(TerrainChunkCoord coord)
        {
            Assert.IsTrue(_renderer.TryGetChunkObject(coord, out GameObject chunk), "chunk " + coord + " has an object");
            return chunk.GetComponent<MeshFilter>().sharedMesh;
        }

        /// <summary>The smallest alpha among the vertices of the up-facing faces whose top lies at cell row <paramref name="topRow"/>.</summary>
        private static byte UpFaceAlpha(Mesh mesh, int topRow, int cellX, int cellZ, bool smallest)
        {
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Color32[] colors = mesh.colors32;
            byte result = smallest ? byte.MaxValue : byte.MinValue;
            bool found = false;
            for (int index = 0; index < vertices.Length; index += 4)
            {
                if (normals[index].y < 0.5f)
                {
                    continue;
                }

                RbxVector3 centre = RbxSpace.FromUnity((vertices[index] + vertices[index + 2]) * 0.5f);
                if (Mathf.RoundToInt(centre.Y / 4f) != topRow || Mathf.FloorToInt(centre.X / 4f) != cellX
                    || Mathf.FloorToInt(centre.Z / 4f) != cellZ)
                {
                    continue;
                }

                for (int corner = 0; corner < 4; corner++)
                {
                    byte alpha = colors[index + corner].a;
                    result = smallest ? (byte)Mathf.Min(result, alpha) : (byte)Mathf.Max(result, alpha);
                }

                found = true;
            }

            Assert.IsTrue(found, "an up face at row " + topRow + " over cell (" + cellX + ", " + cellZ + ")");
            return result;
        }

        [Test]
        public void SkyExposure_ChunkMesh_CarriesTheExposureInTheVertexColourAlpha()
        {
            Fill(-16, -4, -16, 48, 4, 32, Slot("Ground"));
            Fill(0, 0, -16, 16, 8, 32, Slot("Rock"));
            Fill(0, 0, 0, 14, 2, 3, TerrainMaterials.Air);

            Drain();

            Mesh ground = SolidMesh(new TerrainChunkCoord(0, -1, 0));
            Assert.AreEqual(ground.vertexCount, ground.colors32.Length, "one colour per vertex");
            Assert.AreEqual(255, UpFaceAlpha(SolidMesh(new TerrainChunkCoord(-1, -1, 0)), 0, -4, 1, true),
                "the open ground beside the hill");
            Assert.AreEqual(0, UpFaceAlpha(ground, 0, 12, 1, false), "the tunnel floor twelve cells in");
            byte mouth = UpFaceAlpha(ground, 0, 0, 1, false);
            Assert.Greater(mouth, 0, "the floor at the mouth sees some sky");
            Assert.Less(mouth, 255);
            Mesh hill = SolidMesh(new TerrainChunkCoord(0, 0, 0));
            Assert.AreEqual(255, UpFaceAlpha(hill, 8, 8, 1, true), "the hill top straight above the tunnel");
            foreach (Color32 color in hill.colors32)
            {
                Assert.IsTrue(color.r == color.g && color.g == color.b, "RGB is the grey corner shade");
                Assert.Contains(color.r, TerrainSkyExposure.CornerShadeLevels,
                    "a whole face corner is one of the corner shade levels");
            }
        }

        [Test]
        public void SkyExposure_AWorldWithoutCaves_MeshesAsBefore_ApartFromTheNewChannel()
        {
            byte grass = Slot("Grass");
            for (int x = -24; x < 24; x++)
            {
                for (int z = -24; z < 24; z++)
                {
                    int height = 2 + (x * x + z * 3) % 7 / 2 + (x > 6 ? 3 : 0);
                    Fill(x, -4, z, 1, 4 + height, 1, grass);
                }
            }

            Fill(-28, -4, -28, 3, 3, 56, TerrainMaterials.Water);
            Drain();
            TerrainChunkMeshBuffers buffers = new();
            RbxTerrainMeshBuilder builder = new();
            List<TerrainChunkCoord> coords = new();
            _terrain.Store.CopyChunkCoords(coords);
            int checkedChunks = 0;
            foreach (TerrainChunkCoord coord in coords)
            {
                if (!_renderer.TryGetChunkObject(coord, out GameObject chunk))
                {
                    continue;
                }

                new BlockyTerrainChunkMesher().BuildChunk(_terrain.Store, coord, buffers);
                Mesh before = new();
                _owned.Add(before);
                builder.BuildSolid(buffers, before, out int _);
                Mesh after = chunk.GetComponent<MeshFilter>().sharedMesh;

                CollectionAssert.AreEqual(before.vertices, after.vertices, "vertices of " + coord);
                CollectionAssert.AreEqual(before.normals, after.normals, "normals of " + coord);
                Assert.AreEqual(before.subMeshCount, after.subMeshCount);
                for (int submesh = 0; submesh < before.subMeshCount; submesh++)
                {
                    // WHY per quad: the corner shade may split a quad along its other diagonal; it is still the same
                    // four corners.
                    CollectionAssert.AreEqual(QuadCorners(before.GetIndices(submesh)),
                        QuadCorners(after.GetIndices(submesh)), "quads of " + coord);
                }

                Color32[] colors = after.colors32;
                Vector3[] normals = after.normals;
                Assert.AreEqual(after.vertexCount, colors.Length);
                for (int vertex = 0; vertex < colors.Length; vertex++)
                {
                    // WHY not the faces looking down: the underside of the plate faces covered air under it.
                    if (normals[vertex].y > -0.5f)
                    {
                        Assert.AreEqual(255, colors[vertex].a, "every top and side face sees the open sky");
                    }
                }

                checkedChunks++;
            }

            Assert.Greater(checkedChunks, 4);
        }

        [Test]
        public void SkyExposure_ChunkMaterials_AreCopiesThatReadTheVertexExposure_APartsMaterialIsNot()
        {
            RbxTextureMaterialProvider catalog = new();
            RbxTerrain terrain = _terrain;
            _renderer.Dispose();
            _renderer = new RbxTerrainRenderer(terrain, _host.transform, catalog);
            try
            {
                Fill(0, 0, 0, 2, 1, 1, Slot("Grass"));
                Fill(0, 1, 0, 1, 1, 1, TerrainMaterials.Water);
                Drain();
                RbxMaterialId grassId = TerrainMaterials.ToMaterialId(Slot("Grass"));
                RbxMaterialId waterId = TerrainMaterials.ToMaterialId(TerrainMaterials.Water);
                catalog.TryGetMaterial(in grassId, out Material partGrass);
                catalog.TryGetMaterial(in waterId, out Material partWater);

                Assert.IsTrue(_renderer.TryGetChunkObject(new TerrainChunkCoord(0, 0, 0), out GameObject chunk));
                Material chunkGrass = chunk.GetComponent<MeshRenderer>().sharedMaterial;
                Material chunkWater = chunk.transform.GetChild(0).GetComponent<MeshRenderer>().sharedMaterial;
                Assert.AreNotSame(partGrass, chunkGrass);
                Assert.AreSame(partGrass.shader, chunkGrass.shader);
                Assert.AreEqual(partGrass.name, chunkGrass.name);
                Assert.AreSame(partGrass.GetTexture("_BaseMap"), chunkGrass.GetTexture("_BaseMap"));
                Assert.AreEqual(1f, chunkGrass.GetFloat(RbxTerrainRenderer.SkyExposureSourceProperty));
                Assert.AreEqual(0f, partGrass.GetFloat(RbxTerrainRenderer.SkyExposureSourceProperty),
                    "a Part never reads a vertex colour it does not have");
                Assert.AreEqual(1f, chunkWater.GetFloat(RbxTerrainRenderer.SkyExposureSourceProperty));
                Assert.AreEqual(0f, partWater.GetFloat(RbxTerrainRenderer.SkyExposureSourceProperty));
                Assert.AreEqual(1f, partGrass.GetFloat(RbxPartSkyExposurePass.SkyExposureProperty),
                    "a part is fully exposed until the pass writes otherwise");
            }
            finally
            {
                _renderer.Dispose();
                _renderer = null;
                catalog.Dispose();
            }
        }

        [Test]
        public void SkyExposure_OpeningAShaft_RelightsTheTunnelBelow_InTheSamePass_WithoutRemeshingIt()
        {
            Fill(-16, -4, -16, 64, 4, 32, Slot("Ground"));
            Fill(0, 0, -16, 32, 10, 32, Slot("Rock"));
            Fill(0, 0, 0, 30, 2, 3, TerrainMaterials.Air);
            Drain();
            Mesh ground = SolidMesh(new TerrainChunkCoord(1, -1, 0));
            Assert.AreEqual(0, UpFaceAlpha(ground, 0, 24, 1, false), "dark before the shaft");
            long remeshes = _renderer.RemeshCount;
            long relights = _renderer.LightRefreshCount;

            Fill(24, 2, 1, 1, 8, 1, TerrainMaterials.Air);
            _renderer.ProcessDirty(int.MaxValue, double.MaxValue);

            Assert.AreEqual(0, _renderer.PendingChunkCount);
            Assert.AreEqual(1, _renderer.RemeshCount - remeshes, "only the hill chunk the shaft was dug in is re-meshed");
            Assert.GreaterOrEqual(_renderer.LightRefreshCount - relights, 1);
            CollectionAssert.Contains(_renderer.LastProcessedChunks, new TerrainChunkCoord(1, -1, 0));
            Assert.Greater(UpFaceAlpha(ground, 0, 24, 1, false), 150, "the floor under the shaft sees the sky");
            Assert.Greater(UpFaceAlpha(ground, 0, 22, 1, false), 0, "and the floor beside it a little");
            Assert.AreEqual(0, UpFaceAlpha(SolidMesh(new TerrainChunkCoord(0, -1, 0)), 0, 12, 1, false),
                "the middle of the tunnel stays dark");
        }

        [Test]
        public void SkyExposure_EnsureColliders_BuildsOnlyTheQueuedChunksTheBoxTouches()
        {
            Fill(0, 0, 0, 64, 4, 64, Slot("Grass"));
            Assert.AreEqual(16, _renderer.PendingChunkCount);
            Bounds overChunk = new(RbxSpace.ToUnity(new RbxVector3(145f, 20f, 145f)),
                Vector3.one * RbxSpace.LengthToUnity(8f));

            int built = _renderer.EnsureColliders(overChunk);

            Assert.AreEqual(1, built, "the one chunk under the box");
            Assert.AreEqual(15, _renderer.PendingChunkCount);
            Assert.IsTrue(_renderer.TryGetChunkObject(new TerrainChunkCoord(2, 0, 2), out GameObject chunk));
            Assert.IsNotNull(chunk.GetComponent<MeshCollider>().sharedMesh);
            Assert.IsFalse(_renderer.IsGeometryPending(new TerrainChunkCoord(2, 0, 2)));
            Assert.IsTrue(_renderer.IsGeometryPending(new TerrainChunkCoord(0, 0, 0)));
            Assert.AreEqual(0, _renderer.EnsureColliders(overChunk), "nothing left to build there");
            Drain();
            Assert.AreEqual(16, _renderer.ChunkObjectCount, "the skipped queue entry does not build the chunk twice");
        }

        [Test]
        public void SkyExposure_PartPass_DimsARendererInACave_AndWritesNothingInTheOpen()
        {
            Fill(-16, -4, -16, 48, 4, 32, Slot("Ground"));
            Fill(0, 0, -16, 16, 8, 32, Slot("Rock"));
            Fill(0, 0, 0, 14, 2, 3, TerrainMaterials.Air);
            Drain();
            GameObject inCave = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject inOpen = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _owned.Add(inCave);
            _owned.Add(inOpen);
            inCave.transform.position = RbxSpace.ToUnity(new RbxVector3(12f * 4f + 2f, 2f, 1f * 4f + 2f));
            inOpen.transform.position = RbxSpace.ToUnity(new RbxVector3(-8f * 4f, 2f, 1f * 4f + 2f));
            inCave.transform.localScale = Vector3.one * 0.5f;
            inOpen.transform.localScale = Vector3.one * 0.5f;
            Renderer caveRenderer = inCave.GetComponent<Renderer>();
            Renderer openRenderer = inOpen.GetComponent<Renderer>();
            RbxPartSkyExposurePass pass = new();

            int visited = pass.Tick(_renderer.SkyExposure, _terrain.Store.Revision,
                list => list.AddRange(new[] { caveRenderer, openRenderer }), 8);

            Assert.AreEqual(2, visited);
            MaterialPropertyBlock block = new();
            caveRenderer.GetPropertyBlock(block);
            Assert.AreEqual(0f, block.GetFloat(RbxPartSkyExposurePass.SkyExposureProperty), 1e-3f);
            openRenderer.GetPropertyBlock(block);
            Assert.IsFalse(block.HasFloat(RbxPartSkyExposurePass.SkyExposureProperty),
                "a part under the open sky keeps its batching untouched");
            Assert.AreEqual(1, pass.WriteCount);

            Fill(12, 2, 1, 1, 6, 1, TerrainMaterials.Air);
            pass.Tick(_renderer.SkyExposure, _terrain.Store.Revision,
                list => list.AddRange(new[] { caveRenderer, openRenderer }), 8);
            caveRenderer.GetPropertyBlock(block);
            Assert.AreEqual(1f, block.GetFloat(RbxPartSkyExposurePass.SkyExposureProperty), 1e-3f,
                "a shaft opened over it lights it on the next sweep");
        }

        [Test]
        public void SkyExposure_PartPass_APartThatLeftTheCave_IsWrittenAgainAtOnce_NotOnItsTurn()
        {
            Renderer[] parts = CaveWorldWithParts(out Renderer walker);
            RbxPartSkyExposurePass pass = new();
            pass.Tick(_renderer.SkyExposure, _terrain.Store.Revision, list => list.AddRange(parts), 8);
            MaterialPropertyBlock block = new();
            walker.GetPropertyBlock(block);
            Assert.AreEqual(0f, block.GetFloat(RbxPartSkyExposurePass.SkyExposureProperty), 1e-3f, "dark in the cave");

            walker.transform.position = RbxSpace.ToUnity(new RbxVector3(-6f * 4f + 2f, 2f, 1f * 4f + 2f));
            int visited = pass.Tick(_renderer.SkyExposure, _terrain.Store.Revision, list => list.AddRange(parts), 1);

            walker.GetPropertyBlock(block);
            Assert.AreEqual(1f, block.GetFloat(RbxPartSkyExposurePass.SkyExposureProperty), 1e-3f,
                "out in the open after one step, though its turn is the last of the sweep");
            Assert.AreEqual(1, pass.OutOfTurnCount);
            Assert.AreEqual(2, visited, "the moved part and the one part the budget allows in turn");
        }

        [Test]
        public void SkyExposure_PartPass_AnUnmovedPartInTheCave_WaitsForItsTurn_NegativeTwin()
        {
            Renderer[] parts = CaveWorldWithParts(out Renderer walker);
            RbxPartSkyExposurePass pass = new();
            pass.Tick(_renderer.SkyExposure, _terrain.Store.Revision, list => list.AddRange(parts), 8);
            long writes = pass.WriteCount;

            walker.transform.position += new Vector3(0.01f, 0f, 0f);
            int visited = pass.Tick(_renderer.SkyExposure, _terrain.Store.Revision, list => list.AddRange(parts), 1);

            Assert.AreEqual(0, pass.OutOfTurnCount, "still in the same cell: nothing is applied outside its turn");
            Assert.AreEqual(writes, pass.WriteCount);
            Assert.AreEqual(1, visited, "only the budget");
        }

        /// <summary>
        /// The cave world of <see cref="SkyExposure_PartPass_DimsARendererInACave_AndWritesNothingInTheOpen"/> with
        /// three parts in the open and <paramref name="walker"/> in the cave, last in the sweep.
        /// </summary>
        private Renderer[] CaveWorldWithParts(out Renderer walker)
        {
            Fill(-16, -4, -16, 48, 4, 32, Slot("Ground"));
            Fill(0, 0, -16, 16, 8, 32, Slot("Rock"));
            Fill(0, 0, 0, 14, 2, 3, TerrainMaterials.Air);
            Drain();
            Renderer[] parts = new Renderer[4];
            for (int index = 0; index < parts.Length; index++)
            {
                GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _owned.Add(part);
                part.transform.localScale = Vector3.one * 0.5f;
                part.transform.position = index < 3
                    ? RbxSpace.ToUnity(new RbxVector3((-12f + index) * 4f + 2f, 2f, 1f * 4f + 2f))
                    : RbxSpace.ToUnity(new RbxVector3(12f * 4f + 2f, 2f, 1f * 4f + 2f));
                parts[index] = part.GetComponent<Renderer>();
            }

            walker = parts[3];
            return parts;
        }

        [Test]
        public void FaceRule_AGrassCell_DrawsGrassOnTopAndEarthOnTheSides_AndStillHitsAsGrass()
        {
            Fill(0, 0, 0, 1, 1, 1, Slot("Grass"));
            Drain();

            Assert.IsTrue(_renderer.TryGetChunkObject(new TerrainChunkCoord(0, 0, 0), out GameObject chunk));
            Material[] materials = chunk.GetComponent<MeshRenderer>().sharedMaterials;
            Assert.AreEqual(new[] { "Grass", "Ground" }, new[] { materials[0].name, materials[1].name });
            Mesh mesh = chunk.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreEqual(4 * 5, mesh.GetIndices(0).Length / 6 * 4, "the top and four bands are grass");
            Assert.AreEqual(4 * 5, mesh.GetIndices(1).Length / 6 * 4, "four sides below the bands and the bottom are earth");
            float bandBottom = float.MaxValue;
            int[] grassIndices = mesh.GetIndices(0);
            Vector3[] vertices = mesh.vertices;
            foreach (int index in grassIndices)
            {
                bandBottom = Mathf.Min(bandBottom, RbxSpace.FromUnity(vertices[index]).Y);
            }

            Assert.AreEqual(4f * 13f / 16f, bandBottom, 1e-3f, "the band reaches three sixteenths of the cell down");
            Assert.IsTrue(_renderer.TryResolveHit(chunk.GetComponent<MeshCollider>(),
                RbxSpace.ToUnity(new RbxVector3(4f, 1f, 2f)), RbxSpace.DirectionToUnity(new RbxVector3(1f, 0f, 0f)),
                out InstanceId _, out RbxMaterialId material, out RbxVector3 _));
            Assert.AreEqual("Grass", material.Name, "a hit on the earth-drawn side still reports the Grass cell");
        }

        /// <summary>One plain material per Enum.Material, from a shader without the exposure input.</summary>
        private sealed class PlainMaterials : IRbxMaterialProvider<Material>
        {
            private readonly Dictionary<int, Material> _byValue = new();
            private readonly List<Object> _owned;

            public PlainMaterials(List<Object> owned)
            {
                _owned = owned;
                FallbackMaterial = Create("Fallback");
            }

            public Material FallbackMaterial { get; }

            public bool TryGetMaterial(in RbxMaterialId material, out Material visualMaterial)
            {
                if (!_byValue.TryGetValue(material.Value, out visualMaterial))
                {
                    visualMaterial = Create(material.Name);
                    _byValue.Add(material.Value, visualMaterial);
                }

                return true;
            }

            private Material Create(string name)
            {
                Material material = new(Shader.Find("Hidden/InternalErrorShader")) { name = name };
                _owned.Add(material);
                return material;
            }
        }
    }
}
