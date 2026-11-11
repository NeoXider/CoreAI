using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Terrain
{
    /// <summary>
    /// <c>workspace:Raycast</c> on a host without a physics engine (the null physics port: a server, a headless tool,
    /// a test) answers Terrain from the engine-free voxel march, under the filter rules the Unity port applies, and
    /// the nearer of a terrain cell and a port hit wins. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class HeadlessTerrainRaycastEditModeTests
    {
        private static readonly byte Grass = P4TerrainTestWorld.Slot("Grass");

        private static RbxWorldPhysics HeadlessWorld(out RbxTerrain terrain, out InstanceRegistry registry)
        {
            terrain = P4TerrainTestWorld.CreateTerrain(out registry, TerrainLimits.Desktop);
            terrain.FillBlock(RbxCFrame.FromPosition(0f, -2f, 0f), new RbxVector3(64f, 4f, 64f), Grass);
            return new RbxWorldPhysics(registry);
        }

        [Test]
        public void Headless_ARayDownOntoTerrain_HitsTheCell_WithTheUnityPortsAnswer()
        {
            RbxWorldPhysics physics = HeadlessWorld(out RbxTerrain terrain, out InstanceRegistry _);
            Assert.IsInstanceOf<NullRbxPhysicsPort>(physics.Port, "no engine is attached");

            RbxRaycastResult result = physics.Raycast(new RbxVector3(1f, 10f, 1f), new RbxVector3(0f, -50f, 0f), null);

            Assert.IsNotNull(result, "a server or headless tool sees the blocks");
            Assert.AreSame(terrain, result.Instance);
            Assert.AreEqual("Grass", result.Material.Name);
            Assert.AreEqual(new RbxVector3(0f, 1f, 0f), result.Normal);
            Assert.AreEqual(10d, result.Distance, 1e-4d);
        }

        [Test]
        public void Headless_Negative_ExcludingTheTerrain_Misses_AndARayOverEmptyCellsMisses()
        {
            RbxWorldPhysics physics = HeadlessWorld(out RbxTerrain terrain, out InstanceRegistry _);
            RbxRaycastParams exclude = new();
            exclude.AddToFilter(terrain);
            RbxRaycastParams include = new() { FilterType = RbxRaycastFilterType.Include };
            include.AddToFilter(terrain);

            Assert.IsNull(physics.Raycast(new RbxVector3(1f, 10f, 1f), new RbxVector3(0f, -50f, 0f), exclude));
            Assert.IsNotNull(physics.Raycast(new RbxVector3(1f, 10f, 1f), new RbxVector3(0f, -50f, 0f), include),
                "twin: an Include filter naming the Terrain hits it");
            Assert.IsNull(physics.Raycast(new RbxVector3(1f, 10f, 1f), new RbxVector3(0f, 50f, 0f), null),
                "twin: a ray up into empty cells misses");
        }

        [Test]
        public void Headless_RespectCanCollide_DoesNotHideTerrain_WhichAlwaysCollides()
        {
            RbxWorldPhysics physics = HeadlessWorld(out RbxTerrain terrain, out InstanceRegistry _);
            RbxRaycastParams respect = new() { RespectCanCollide = true };

            Assert.AreSame(terrain,
                physics.Raycast(new RbxVector3(1f, 10f, 1f), new RbxVector3(0f, -50f, 0f), respect).Instance);
        }

        [Test]
        public void Headless_Negative_AnEnginePort_IsAskedAlone_SoItsOwnTerrainAnswerIsNotDoubled()
        {
            RbxWorldPhysics physics = HeadlessWorld(out RbxTerrain _, out InstanceRegistry _);
            physics.AttachPort(new MissEverythingPort());

            Assert.IsNull(physics.Raycast(new RbxVector3(1f, 10f, 1f), new RbxVector3(0f, -50f, 0f), null),
                "a host with an engine answers for its chunk colliders itself");
        }

        private sealed class MissEverythingPort : IRbxPhysicsPort
        {
            public event System.Action<InstanceId, InstanceId> ContactBegan
            {
                add { }
                remove { }
            }

            public event System.Action<InstanceId, InstanceId> ContactEnded
            {
                add { }
                remove { }
            }

            public bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds, bool respectCanCollide,
                System.Func<InstanceId, bool> isEligible, out RbxPhysicsRaycastHit hit)
            {
                hit = default;
                return false;
            }

            public void SetGravity(double studsPerSecondSquared)
            {
            }
        }
    }
}
