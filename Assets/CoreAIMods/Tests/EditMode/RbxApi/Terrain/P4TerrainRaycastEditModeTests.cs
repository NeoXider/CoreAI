using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// P4, engine-free: a ray onto Terrain answers <c>Instance = workspace.Terrain</c>, the hit cell's
    /// <c>Material</c> and the face <c>Normal</c>; <c>RaycastParams</c> filters include or exclude the
    /// Terrain like any instance. The physics port is a fake over the voxel ray march, standing in for
    /// the chunk colliders a Unity world casts against.
    /// </summary>
    [TestFixture]
    public sealed class P4TerrainRaycastEditModeTests
    {
        private static readonly byte Grass = P4TerrainTestWorld.Slot("Grass");

        private sealed class TerrainOnlyPhysicsPort : IRbxPhysicsPort
        {
            private readonly RbxTerrain _terrain;

            public TerrainOnlyPhysicsPort(RbxTerrain terrain)
            {
                _terrain = terrain;
            }

            public event Action<InstanceId, InstanceId> ContactBegan
            {
                add { }
                remove { }
            }

            public event Action<InstanceId, InstanceId> ContactEnded
            {
                add { }
                remove { }
            }

            public bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds, bool respectCanCollide,
                Func<InstanceId, bool> isEligible, out RbxPhysicsRaycastHit hit)
            {
                hit = default;
                if (!isEligible(_terrain.Id)
                    || !TerrainVoxelRaycaster.TryRaycast(_terrain.Store, originStuds, directionStuds,
                        out TerrainRaycastHit cell))
                {
                    return false;
                }

                hit = new RbxPhysicsRaycastHit(_terrain.Id, cell.Position, cell.Normal,
                    TerrainMaterials.ToMaterialId(cell.Material), cell.Distance);
                return true;
            }

            public void SetGravity(double studsPerSecondSquared)
            {
            }
        }

        private static RbxWorldPhysics World(out RbxTerrain terrain)
        {
            terrain = P4TerrainTestWorld.CreateTerrain(out InstanceRegistry registry, TerrainLimits.Desktop);
            terrain.FillBlock(RbxCFrame.FromPosition(0f, -2f, 0f), new RbxVector3(64f, 4f, 64f), Grass);
            RbxWorldPhysics physics = new(registry);
            physics.AttachPort(new TerrainOnlyPhysicsPort(terrain));
            return physics;
        }

        [Test]
        public void P4_Ray_StraightDownOntoGrass_AnswersTerrainGrassAndTheUpNormal()
        {
            RbxWorldPhysics physics = World(out RbxTerrain terrain);

            RbxRaycastResult result = physics.Raycast(new RbxVector3(1f, 10f, 1f), new RbxVector3(0f, -50f, 0f),
                null);

            Assert.IsNotNull(result);
            Assert.AreSame(terrain, result.Instance);
            Assert.AreEqual("Grass", result.Material.Name);
            Assert.AreEqual(1280, result.Material.Value);
            Assert.AreEqual(new RbxVector3(0f, 1f, 0f), result.Normal);
            Assert.AreEqual(0f, result.Position.Y, 1e-4f, "the top face of the slab is y = 0");
            Assert.AreEqual(10d, result.Distance, 1e-4d);
        }

        [Test]
        public void P4_Ray_ExcludingTheTerrain_Misses_AndIncludingItHits()
        {
            RbxWorldPhysics physics = World(out RbxTerrain terrain);
            RbxRaycastParams exclude = new();
            exclude.AddToFilter(terrain);
            RbxRaycastParams include = new() { FilterType = RbxRaycastFilterType.Include };
            include.AddToFilter(terrain);

            Assert.IsNull(physics.Raycast(new RbxVector3(1f, 10f, 1f), new RbxVector3(0f, -50f, 0f), exclude));
            Assert.AreSame(terrain,
                physics.Raycast(new RbxVector3(1f, 10f, 1f), new RbxVector3(0f, -50f, 0f), include).Instance);
        }

        [Test]
        public void P4_Ray_IntoTheSideOfABlock_AnswersThatFace()
        {
            RbxWorldPhysics physics = World(out RbxTerrain terrain);
            terrain.FillBlock(RbxCFrame.FromPosition(RbxTerrain.CellCenterToWorld(3, 0, 0)), new RbxVector3(4f, 4f, 4f),
                P4TerrainTestWorld.Slot("Brick"));

            RbxRaycastResult result = physics.Raycast(new RbxVector3(-20f, 2f, 2f), new RbxVector3(100f, 0f, 0f),
                null);

            Assert.AreEqual("Brick", result.Material.Name);
            Assert.AreEqual(new RbxVector3(-1f, 0f, 0f), result.Normal);
            Assert.AreEqual(12f, result.Position.X, 1e-4f);
        }

        [Test]
        public void P4_Ray_PassesThroughWater_AndMissesEmptyTerrain()
        {
            RbxWorldPhysics physics = World(out RbxTerrain terrain);
            terrain.FillBlock(RbxCFrame.FromPosition(0f, 6f, 0f), new RbxVector3(64f, 8f, 64f), TerrainMaterials.Water);

            RbxRaycastResult throughWater = physics.Raycast(new RbxVector3(1f, 20f, 1f),
                new RbxVector3(0f, -50f, 0f), null);
            Assert.AreEqual("Grass", throughWater.Material.Name, "water has no collider in a block world");

            terrain.Clear();
            Assert.IsNull(physics.Raycast(new RbxVector3(1f, 20f, 1f), new RbxVector3(0f, -50f, 0f), null));
        }

        [Test]
        public void P4_Terrain_IsHostProtected_WritableByAnyActor_NeverDestroyed()
        {
            RbxTerrain terrain = P4TerrainTestWorld.CreateTerrain(out InstanceRegistry registry);
            Assert.IsTrue(registry.TryGetRecord(terrain.Id, out InstanceRecord record));
            Assert.AreEqual(InstanceAccessScope.HostProtected, record.AccessScope);
            Assert.IsTrue(record.IsRuntimeInfrastructure, "rebuilt by the bootstrap, never stored in the package tree");
            InstanceRegistry strict = new(P4TerrainTestWorld.CatalogWithTerrain(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            RbxInstance workspace = DataModelBootstrap.CreateGame(strict).FindFirstChildOfClass("Workspace");
            RbxTerrain guarded = RbxTerrainBootstrap.EnsureTerrain(strict, workspace);

            Assert.DoesNotThrow(() => WorldAclAuthorizer.Demand(strict, "builder", false, "", guarded,
                WorldAclDecision.WriteProperty, "write terrain"), "any actor may build");
            Assert.Throws<RbxError>(() => WorldAclAuthorizer.Demand(strict, "host", true, "", guarded,
                WorldAclDecision.Destroy, "destroy"), "not even the host destroys the Terrain");
            Assert.AreSame(guarded, RbxTerrainBootstrap.EnsureTerrain(strict, workspace), "one Terrain per Workspace");
        }

        [Test]
        public void P4_HitResolution_FromAFacePointAndNormal_FindsTheCellBehindIt()
        {
            TerrainChunkStore store = new(TerrainLimits.Desktop);
            store.SetCell(2, 0, -1, Grass, TerrainOccupancy.Full);

            Assert.IsTrue(TerrainRaycastMath.TryResolveHit(store, new RbxVector3(10f, 4f, -2f),
                new RbxVector3(0.0001f, 0.9999f, 0f), out int x, out int y, out int z, out byte material,
                out RbxVector3 normal));
            Assert.AreEqual((2, 0, -1), (x, y, z));
            Assert.AreEqual(Grass, material);
            Assert.AreEqual(new RbxVector3(0f, 1f, 0f), normal, "the normal is snapped to the face axis");

            Assert.IsFalse(TerrainRaycastMath.TryResolveHit(store, new RbxVector3(10f, 4f, -2f),
                new RbxVector3(0f, -1f, 0f), out _, out _, out _, out _, out _),
                "a point whose cell behind the face is Air is not a Terrain hit");
        }
    }
}
