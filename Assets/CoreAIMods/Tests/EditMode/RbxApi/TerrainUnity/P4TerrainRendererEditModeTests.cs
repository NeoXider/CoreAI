using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace CoreAI.Tests.EditMode.RbxApi.TerrainUnity
{
    /// <summary>
    /// P4, Unity EditMode: the default Terrain renderer. Mesh output for known cells (vertices, winding,
    /// submeshes per material, materials from the catalog), face culling, the water surface on its own
    /// object without a collider, re-meshing only the chunks a write touched, the hit resolver behind
    /// <c>workspace:Raycast</c>, and the presenter with a replaced renderer.
    /// </summary>
    [TestFixture]
    public sealed class P4TerrainRendererEditModeTests
    {
        private readonly List<Object> _owned = new();
        private RbxTerrain _terrain;
        private RbxTerrainRenderer _renderer;
        private NamedMaterialProvider _materials;
        private GameObject _host;

        /// <summary>One shared material per Enum.Material, named after it, from a shader every editor has.</summary>
        private sealed class NamedMaterialProvider : IRbxMaterialProvider<Material>
        {
            private readonly Dictionary<int, Material> _byValue = new();
            private readonly List<Object> _owned;

            public NamedMaterialProvider(List<Object> owned)
            {
                _owned = owned;
                FallbackMaterial = Create("Fallback");
            }

            public Material FallbackMaterial { get; }

            public int Lookups { get; private set; }

            public bool TryGetMaterial(in RbxMaterialId material, out Material visualMaterial)
            {
                Lookups++;
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

        private sealed class CountingRenderer : IRbxTerrainRenderer
        {
            public CountingRenderer(RbxTerrain terrain)
            {
                Terrain = terrain;
            }

            public RbxTerrain Terrain { get; }

            public int PendingChunkCount => 0;

            public int Passes { get; private set; }

            public bool Disposed { get; private set; }

            public int ProcessDirty(int maxChunks, double maxMilliseconds)
            {
                Passes++;
                return 0;
            }

            public bool TryResolveHit(Collider collider, Vector3 pointUnity, Vector3 normalUnity,
                out InstanceId terrainId, out RbxMaterialId material, out RbxVector3 normalStuds)
            {
                terrainId = default;
                material = default;
                normalStuds = default;
                return false;
            }

            public void Dispose()
            {
                Disposed = true;
            }
        }

        [SetUp]
        public void CreateWorld()
        {
            _terrain = NewTerrain();
            _host = new GameObject("P4 terrain host");
            _owned.Add(_host);
            _materials = new NamedMaterialProvider(_owned);
            _renderer = new RbxTerrainRenderer(_terrain, _host.transform, _materials);
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

        private static RbxTerrain NewTerrain()
        {
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            if (!catalog.TryGet(RbxTerrain.ClassNameValue, out ClassDescriptor _))
            {
                catalog.Register(RbxTerrain.CreateDescriptor());
            }

            InstanceRegistry registry = new(catalog);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            return RbxTerrainBootstrap.EnsureTerrain(registry, game.FindFirstChildOfClass("Workspace"),
                TerrainLimits.Desktop);
        }

        private static byte Slot(string name)
        {
            TerrainMaterials.TryGetSlot(name, out byte slot);
            return slot;
        }

        private void Drain()
        {
            while (_renderer.PendingChunkCount > 0)
            {
                _renderer.ProcessDirty(int.MaxValue, double.MaxValue);
            }
        }

        private Mesh SolidMesh(TerrainChunkCoord coord)
        {
            Assert.IsTrue(_renderer.TryGetChunkObject(coord, out GameObject chunk), "chunk " + coord + " has an object");
            return chunk.GetComponent<MeshFilter>().sharedMesh;
        }

        [Test]
        public void P4_Renderer_OneCell_IsOneCubeOfItsMaterial_WithAMatchingCollider()
        {
            _terrain.Store.SetCell(1, 2, 3, Slot("Grass"), TerrainOccupancy.Full);

            Drain();

            Assert.IsTrue(_renderer.TryGetChunkObject(new TerrainChunkCoord(0, 0, 0), out GameObject chunk));
            Mesh mesh = chunk.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreEqual(24, mesh.vertexCount);
            Assert.AreEqual(1, mesh.subMeshCount);
            Assert.AreEqual(36, mesh.GetIndices(0).Length);
            MeshRenderer renderer = chunk.GetComponent<MeshRenderer>();
            Assert.AreEqual(1, renderer.sharedMaterials.Length);
            Assert.AreEqual("Grass", renderer.sharedMaterials[0].name);
            Assert.AreSame(mesh, chunk.GetComponent<MeshCollider>().sharedMesh);
            Assert.IsFalse(chunk.GetComponent<MeshCollider>().isTrigger, "chunk colliders are solid");
            Assert.AreEqual(Vector3.zero, chunk.transform.position, "vertices are world-space on an identity chunk");
            Bounds expected = new();
            expected.SetMinMax(RbxSpace.ToUnity(new RbxVector3(4f, 8f, 16f)),
                RbxSpace.ToUnity(new RbxVector3(8f, 12f, 12f)));
            Assert.AreEqual(expected.min.x, mesh.bounds.min.x, 1e-4f);
            Assert.AreEqual(expected.max.y, mesh.bounds.max.y, 1e-4f);
            Assert.AreEqual(-16f * RbxSpace.MetersPerStud, mesh.bounds.min.z, 1e-4f, "Roblox +Z is Unity -Z");
        }

        [Test]
        public void P4_Renderer_EveryTriangle_FacesOutward_AfterTheZMirror()
        {
            _terrain.Store.SetCell(0, 0, 0, Slot("Rock"), TerrainOccupancy.Full);
            Drain();
            Mesh mesh = SolidMesh(new TerrainChunkCoord(0, 0, 0));
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            int[] triangles = mesh.GetIndices(0);
            Vector3 centre = mesh.bounds.center;

            for (int index = 0; index < triangles.Length; index += 3)
            {
                Vector3 a = vertices[triangles[index]];
                Vector3 b = vertices[triangles[index + 1]];
                Vector3 c = vertices[triangles[index + 2]];
                Vector3 front = Vector3.Cross(b - a, c - a);
                Assert.Greater(Vector3.Dot(front, normals[triangles[index]]), 0f,
                    "Unity treats Cross(b - a, c - a) as the front side; it must agree with the normal");
                Assert.Greater(Vector3.Dot(normals[triangles[index]], (a + b + c) / 3f - centre), 0f,
                    "the normal points away from the cube");
            }
        }

        [Test]
        public void P4_Renderer_TouchingCells_ShareNoFace_AndGetOneSubmeshPerMaterial()
        {
            _terrain.Store.SetCell(0, 0, 0, Slot("Grass"), TerrainOccupancy.Full);
            _terrain.Store.SetCell(1, 0, 0, Slot("Rock"), TerrainOccupancy.Full);

            Drain();

            Mesh mesh = SolidMesh(new TerrainChunkCoord(0, 0, 0));
            Assert.AreEqual(40, mesh.vertexCount, "10 visible faces");
            Assert.AreEqual(2, mesh.subMeshCount);
            Material[] materials = _renderer.Root.GetComponentInChildren<MeshRenderer>().sharedMaterials;
            Assert.AreEqual(new[] { "Rock", "Grass" }, new[] { materials[0].name, materials[1].name },
                "submeshes follow the slot order");
        }

        [Test]
        public void P4_Renderer_Water_DrawsOnItsOwnObject_WithTheWaterMaterial_AndNoCollider()
        {
            _terrain.Store.SetCell(0, 0, 0, Slot("Grass"), TerrainOccupancy.Full);
            _terrain.Store.FillBox(new TerrainCellBox(1, 0, 0, 2, 1, 1), TerrainMaterials.Water, TerrainOccupancy.Full);

            Drain();

            Assert.IsTrue(_renderer.TryGetChunkObject(new TerrainChunkCoord(0, 0, 0), out GameObject chunk));
            Assert.AreEqual(24, chunk.GetComponent<MeshFilter>().sharedMesh.vertexCount,
                "the grass face behind the water is drawn");
            Transform water = chunk.transform.GetChild(0);
            Assert.IsTrue(water.gameObject.activeSelf);
            Assert.AreEqual(4 * 9, water.GetComponent<MeshFilter>().sharedMesh.vertexCount,
                "two cubes of 6 faces minus the two faces they share and the one against the grass");
            Assert.AreEqual("Water", water.GetComponent<MeshRenderer>().sharedMaterial.name);
            Assert.IsNull(water.GetComponent<Collider>(), "water does not block movement");
        }

        [Test]
        public void P4_Renderer_RealCatalog_ResolvesWaterThroughTheMaterialProvider()
        {
            RbxTextureMaterialProvider catalog = new();
            RbxTerrain terrain = NewTerrain();
            RbxTerrainRenderer renderer = new(terrain, _host.transform, catalog);
            try
            {
                terrain.Store.SetCell(0, 0, 0, TerrainMaterials.Water, TerrainOccupancy.Full);
                renderer.ProcessDirty(int.MaxValue, double.MaxValue);
                RbxMaterialId water = TerrainMaterials.ToMaterialId(TerrainMaterials.Water);
                catalog.TryGetMaterial(in water, out Material expected);

                Assert.IsTrue(renderer.TryGetChunkObject(new TerrainChunkCoord(0, 0, 0), out GameObject chunk));
                Assert.AreSame(expected, chunk.transform.GetChild(0).GetComponent<MeshRenderer>().sharedMaterial);
                Assert.IsFalse(chunk.GetComponent<MeshRenderer>().enabled, "a chunk of only water has no solid faces");
                Assert.IsFalse(chunk.GetComponent<MeshCollider>().enabled);
            }
            finally
            {
                renderer.Dispose();
                catalog.Dispose();
            }
        }

        [Test]
        public void P4_Renderer_PlacingOneBorderCell_RemeshesOnlyItsChunkAndTheFaceNeighbour()
        {
            _terrain.Store.FillBox(new TerrainCellBox(-16, -16, -16, 48, 48, 48), Slot("Rock"), TerrainOccupancy.Full);
            Drain();
            long before = _renderer.RemeshCount;

            _terrain.Store.SetCell(0, 5, 5, TerrainMaterials.Air, 0);
            _renderer.ProcessDirty(int.MaxValue, double.MaxValue);

            Assert.AreEqual(2, _renderer.RemeshCount - before);
            CollectionAssert.AreEquivalent(
                new[] { new TerrainChunkCoord(-1, 0, 0), new TerrainChunkCoord(0, 0, 0) },
                _renderer.LastProcessedChunks);
            Assert.AreEqual(0, _renderer.PendingChunkCount);
        }

        [Test]
        public void P4_Renderer_BreakingTheLastCell_RemovesTheChunkObject()
        {
            _terrain.Store.SetCell(3, 3, 3, Slot("Sand"), TerrainOccupancy.Full);
            Drain();
            Assert.AreEqual(1, _renderer.ChunkObjectCount);

            _terrain.Store.SetCell(3, 3, 3, TerrainMaterials.Air, 0);
            Drain();

            Assert.AreEqual(0, _renderer.ChunkObjectCount);
            Assert.AreEqual(0, _renderer.Root.transform.childCount);
        }

        [Test]
        public void P4_Renderer_Budget_ProcessesOneChunkPerCall_WhenAskedFor()
        {
            _terrain.Store.FillBox(new TerrainCellBox(0, 0, 0, 48, 1, 1), Slot("Brick"), TerrainOccupancy.Full);
            Assert.AreEqual(3, _renderer.PendingChunkCount);

            Assert.AreEqual(1, _renderer.ProcessDirty(1, double.MaxValue));
            Assert.AreEqual(2, _renderer.PendingChunkCount);
            Assert.AreEqual(1, _renderer.ProcessDirty(5, 0d), "a zero time budget still makes progress");
        }

        [Test]
        public void P4_Renderer_AStoreWithCells_IsQueuedWhenTheRendererIsCreated()
        {
            RbxTerrain loaded = NewTerrain();
            loaded.Store.FillBox(new TerrainCellBox(0, 0, 0, 32, 1, 1), Slot("Ground"), TerrainOccupancy.Full);

            RbxTerrainRenderer renderer = new(loaded, _host.transform, _materials);
            try
            {
                Assert.AreEqual(2, renderer.PendingChunkCount);
            }
            finally
            {
                renderer.Dispose();
            }
        }

        [Test]
        public void P4_Renderer_HitResolver_AnswersTerrainMaterialAndAxisNormal_ForItsOwnColliders()
        {
            _terrain.Store.FillBox(new TerrainCellBox(0, 0, 0, 4, 1, 4), Slot("Grass"), TerrainOccupancy.Full);
            Drain();
            Assert.IsTrue(_renderer.TryGetChunkObject(new TerrainChunkCoord(0, 0, 0), out GameObject chunk));
            Collider collider = chunk.GetComponent<MeshCollider>();
            Vector3 pointOnTop = RbxSpace.ToUnity(new RbxVector3(6f, 4f, 6f));

            Assert.IsTrue(_renderer.TryResolveHit(collider, pointOnTop, Vector3.up, out InstanceId id,
                out RbxMaterialId material, out RbxVector3 normal));
            Assert.AreEqual(_terrain.Id, id);
            Assert.AreEqual("Grass", material.Name);
            Assert.AreEqual(new RbxVector3(0f, 1f, 0f), normal);

            GameObject stranger = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _owned.Add(stranger);
            Assert.IsFalse(_renderer.TryResolveHit(stranger.GetComponent<Collider>(), pointOnTop, Vector3.up,
                out _, out _, out _), "a collider the renderer did not build is not Terrain");
        }

        [Test]
        public void P4_Renderer_WarmRemesh_AllocatesNoManagedMemory()
        {
            _terrain.Store.FillBox(new TerrainCellBox(0, 0, 0, 16, 4, 16), Slot("Rock"), TerrainOccupancy.Full);
            Drain();
            byte rock = Slot("Rock");
            byte grass = Slot("Grass");
            _terrain.Store.SetCell(5, 3, 5, grass, TerrainOccupancy.Full);
            Drain();
            _terrain.Store.SetCell(5, 3, 5, rock, TerrainOccupancy.Full);
            Drain();

            Assert.That(() =>
            {
                _terrain.Store.SetCell(5, 3, 5, grass, TerrainOccupancy.Full);
                _renderer.ProcessDirty(int.MaxValue, double.MaxValue);
                _terrain.Store.SetCell(5, 3, 5, rock, TerrainOccupancy.Full);
                _renderer.ProcessDirty(int.MaxValue, double.MaxValue);
            }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void P4_Renderer_Dispose_DestroysEveryChunkObject_AndStopsListening()
        {
            _terrain.Store.SetCell(0, 0, 0, Slot("Grass"), TerrainOccupancy.Full);
            Drain();
            GameObject root = _renderer.Root;

            _renderer.Dispose();
            _terrain.Store.SetCell(1, 0, 0, Slot("Grass"), TerrainOccupancy.Full);

            Assert.IsTrue(root == null, "the root and its chunks are destroyed");
            Assert.AreEqual(0, _renderer.PendingChunkCount, "a disposed renderer no longer queues");
            _renderer = null;
        }

        [Test]
        public void P4_Presenter_BindsTheWorkspaceTerrain_AndAReplacedRendererIsUsed()
        {
            GameObject host = new("P4 presenter host");
            _owned.Add(host);
            List<CountingRenderer> built = new();
            RbxTerrainPresenter presenter = host.AddComponent<RbxTerrainPresenter>();
            presenter.RendererFactory = (terrain, parent) =>
            {
                CountingRenderer fake = new(terrain);
                built.Add(fake);
                return fake;
            };

            presenter.Bind(_terrain);
            presenter.Pump();
            presenter.Bind(_terrain);
            RbxTerrain other = NewTerrain();
            presenter.Bind(other);

            Assert.AreEqual(2, built.Count, "rebinding the same Terrain keeps its renderer");
            Assert.AreEqual(1, built[0].Passes);
            Assert.IsTrue(built[0].Disposed, "binding another Terrain disposes the old renderer");
            Assert.AreSame(other, presenter.Renderer.Terrain);
            presenter.Bind(null);
            Assert.IsTrue(built[1].Disposed);
            Assert.IsNull(presenter.Renderer);
        }

        [Test]
        public void P4_Presenter_ResolvesHits_ThroughTheRendererBoundNow()
        {
            GameObject host = new("P4 resolver host");
            _owned.Add(host);
            RbxTerrainPresenter presenter = host.AddComponent<RbxTerrainPresenter>();
            presenter.RendererFactory = (terrain, parent) => new RbxTerrainRenderer(terrain, parent, _materials);
            Assert.IsFalse(presenter.TryResolveHit(null, Vector3.zero, Vector3.up, out _, out _, out _),
                "nothing bound, nothing resolves");

            presenter.Bind(_terrain);
            _terrain.Store.SetCell(0, 0, 0, Slot("Grass"), TerrainOccupancy.Full);
            presenter.Pump();
            RbxTerrainRenderer bound = (RbxTerrainRenderer)presenter.Renderer;
            Assert.IsTrue(bound.TryGetChunkObject(new TerrainChunkCoord(0, 0, 0), out GameObject chunk));

            Assert.IsTrue(presenter.TryResolveHit(chunk.GetComponent<MeshCollider>(),
                RbxSpace.ToUnity(new RbxVector3(2f, 4f, 2f)), Vector3.up, out InstanceId id, out RbxMaterialId material,
                out _));
            Assert.AreEqual(_terrain.Id, id);
            Assert.AreEqual("Grass", material.Name);
            presenter.Bind(null);
        }

        [Test]
        public void P4_Presenter_Attach_FindsTheTerrainOfTheGame()
        {
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            if (!catalog.TryGet(RbxTerrain.ClassNameValue, out ClassDescriptor _))
            {
                catalog.Register(RbxTerrain.CreateDescriptor());
            }

            InstanceRegistry registry = new(catalog);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxTerrain terrain = RbxTerrainBootstrap.EnsureTerrain(registry, game.FindFirstChildOfClass("Workspace"));
            GameObject host = new("P4 attach host");
            _owned.Add(host);

            RbxTerrainPresenter presenter = RbxTerrainPresenter.Attach(host, game);

            Assert.AreSame(terrain, presenter.Renderer.Terrain);
            Assert.AreSame(presenter, RbxTerrainPresenter.Attach(host, game), "attach is idempotent");
            presenter.Bind(null);
        }
    }
}
