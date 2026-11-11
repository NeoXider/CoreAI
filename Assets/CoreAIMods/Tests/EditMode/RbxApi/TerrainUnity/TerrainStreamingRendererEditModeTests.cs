using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode.RbxApi.TerrainUnity
{
    /// <summary>
    /// The renderer's streaming with a viewer: queued chunks build nearest the viewer first; a chunk past the draw
    /// distance (the end of linear fog) is neither built nor drawn until the viewer comes near, while without a
    /// viewer everything builds (twin); a capped collider request builds at most its cap, nearest the body first;
    /// and a ray builds only the queued chunks it crosses, up to its cap.
    /// </summary>
    [TestFixture]
    public sealed class TerrainStreamingRendererEditModeTests
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
            _host = new GameObject("Streaming renderer host");
            _owned.Add(_host);
            _renderer = new RbxTerrainRenderer(_terrain, _host.transform);
            TerrainMaterials.TryGetSlot("Grass", out byte grass);
            // A floor eight chunks along x: chunks (0..7, 0, 0), one row of them.
            _terrain.Store.FillBox(new TerrainCellBox(0, 0, 0, 128, 4, 16), grass, TerrainOccupancy.Full);
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

        private static Vector3 Cell(float x, float y, float z)
        {
            return RbxSpace.ToUnity(new CoreAI.Mods.Rbx.Datatypes.RbxVector3(x * 4f, y * 4f, z * 4f));
        }

        [Test]
        public void QueuedChunks_BuildNearestTheViewerFirst()
        {
            _renderer.SetViewer(Cell(120f, 8f, 8f), float.PositiveInfinity);

            _renderer.ProcessDirty(1, double.MaxValue);

            Assert.AreEqual(new TerrainChunkCoord(7, 0, 0), _renderer.LastProcessedChunks[0],
                "the chunk under the viewer, the last one queued, is built first");
        }

        [Test]
        public void AChunkPastTheDrawDistance_IsNeitherBuiltNorDrawn_UntilTheViewerComesNear()
        {
            float drawDistance = RbxSpace.LengthToUnity(40f * 4f);
            _renderer.SetViewer(Cell(8f, 8f, 8f), drawDistance);
            _renderer.ProcessDirty(int.MaxValue, double.MaxValue);

            Assert.IsFalse(_renderer.IsGeometryPending(new TerrainChunkCoord(2, 0, 0)), "within 40 cells: built");
            Assert.IsTrue(_renderer.IsGeometryPending(new TerrainChunkCoord(5, 0, 0)), "past it: still queued");
            Assert.AreEqual(4, _renderer.VisibleChunkCount, "chunks 0 to 3 lie within 40 cells");

            _renderer.SetViewer(Cell(100f, 8f, 8f), drawDistance);
            _renderer.ProcessDirty(int.MaxValue, double.MaxValue);

            Assert.IsFalse(_renderer.IsGeometryPending(new TerrainChunkCoord(7, 0, 0)), "the viewer came near: built");
            Assert.IsTrue(_renderer.TryGetChunkObject(new TerrainChunkCoord(0, 0, 0), out GameObject first));
            Assert.IsFalse(first.GetComponent<MeshRenderer>().enabled, "and the chunk now far behind is not drawn");
            Assert.AreEqual(5, _renderer.VisibleChunkCount, "chunks 3 to 7 lie within 40 cells of the viewer now");
        }

        [Test]
        public void WhenTheFogMovesOut_OrIsSwitchedOff_EveryWaitingChunkBuildsAndIsDrawn()
        {
            _renderer.SetViewer(Cell(8f, 8f, 8f), RbxSpace.LengthToUnity(40f * 4f));
            _renderer.ProcessDirty(int.MaxValue, double.MaxValue);
            Assert.IsTrue(_renderer.IsGeometryPending(new TerrainChunkCoord(5, 0, 0)), "past the fog: waiting");

            _renderer.SetViewer(Cell(8f, 8f, 8f), RbxSpace.LengthToUnity(1000f * 4f));
            _renderer.ProcessDirty(int.MaxValue, double.MaxValue);
            Assert.AreEqual(0, _renderer.PendingChunkCount, "the fog moved out: nothing stays unbuilt");
            Assert.AreEqual(8, _renderer.VisibleChunkCount);

            _renderer.SetViewer(Cell(8f, 8f, 8f), RbxSpace.LengthToUnity(40f * 4f));
            Assert.AreEqual(4, _renderer.VisibleChunkCount, "the fog came back in: the far chunks are hidden again");
            _renderer.SetViewer(Cell(8f, 8f, 8f), float.PositiveInfinity);
            Assert.AreEqual(8, _renderer.VisibleChunkCount, "fog off: every chunk is drawn as before");
        }

        [Test]
        public void WithoutAViewer_EveryQueuedChunkBuilds_NegativeTwin()
        {
            _renderer.ProcessDirty(int.MaxValue, double.MaxValue);

            Assert.AreEqual(0, _renderer.PendingChunkCount);
            Assert.AreEqual(8, _renderer.VisibleChunkCount);
        }

        [Test]
        public void ACappedColliderRequest_BuildsAtMostItsCap_NearestTheBodyFirst()
        {
            Bounds everything = new(Cell(64f, 2f, 8f), RbxSpace.SizeToUnity(new CoreAI.Mods.Rbx.Datatypes.RbxVector3(
                512f, 64f, 64f)));
            Bounds body = new(Cell(120f, 6f, 8f), everything.size);

            int built = _renderer.EnsureColliders(body, 2);

            Assert.AreEqual(2, built);
            Assert.IsFalse(_renderer.IsGeometryPending(new TerrainChunkCoord(7, 0, 0)), "the body's own chunk first");
            Assert.IsFalse(_renderer.IsGeometryPending(new TerrainChunkCoord(6, 0, 0)));
            Assert.IsTrue(_renderer.IsGeometryPending(new TerrainChunkCoord(5, 0, 0)), "the rest waits");
            Assert.IsTrue(_renderer.IsGeometryPendingIn(everything));
        }

        [Test]
        public void ARay_BuildsOnlyTheQueuedChunksItCrosses_UpToItsCap()
        {
            Vector3 origin = Cell(2f, 2f, 8f);
            float distance = RbxSpace.LengthToUnity(60f * 4f);

            int built = _renderer.EnsureCollidersAlong(origin, Vector3.right, distance, 2);

            Assert.AreEqual(2, built);
            Assert.IsFalse(_renderer.IsGeometryPending(new TerrainChunkCoord(0, 0, 0)), "where the ray starts");
            Assert.IsFalse(_renderer.IsGeometryPending(new TerrainChunkCoord(1, 0, 0)), "the next one it crosses");
            Assert.IsTrue(_renderer.IsGeometryPending(new TerrainChunkCoord(2, 0, 0)), "past the cap");
        }
    }
}
