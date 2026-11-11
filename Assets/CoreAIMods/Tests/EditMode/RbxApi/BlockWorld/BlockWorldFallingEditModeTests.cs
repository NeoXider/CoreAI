using System.Threading;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the Block World's falling blocks (<c>blockworld_falling</c>) on the real Lua VM and Terrain store: Sand with
    /// air under it falls one block every tenth of a second until it rests, whether its support was broken or it was
    /// placed in the air; a run of five falls as one; it sinks through water and the pool closes over it; a fall cut by
    /// a reload carries on from the runs the mod saved; Grass and Rock never fall; and a generated world has no Sand
    /// hanging over air or water.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldFallingEditModeTests
    {
        private const string Mod = "blockworld_falling";

        private SynchronizationContext _savedContext;
        private BlockWorldTestWorld _world;
        private int _probes;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        /// <summary>
        /// A generated-looking floor: bedrock at row -16 and Ground up to row 0 over cells -3..9, the camera (where the
        /// mod watches from, with no character) above cell (3, 3).
        /// </summary>
        private void StartOnFloor()
        {
            _world = new BlockWorldTestWorld();
            byte bedrock = BlockWorldTestWorld.Slot("Basalt");
            byte ground = BlockWorldTestWorld.Slot("Ground");
            for (int x = -3; x <= 9; x++)
            {
                for (int z = -3; z <= 9; z++)
                {
                    _world.Terrain.Store.SetCell(x, -16, z, bedrock, 255);
                    for (int y = -15; y <= 0; y++)
                    {
                        _world.Terrain.Store.SetCell(x, y, z, ground, 255);
                    }
                }
            }

            _world.LoadSource("test_camera", "local camera = workspace.CurrentCamera "
                                             + "camera.CameraType = Enum.CameraType.Scriptable "
                                             + "camera.CFrame = CFrame.new(14, 14, 14)");
            _world.Load(Mod);
            _world.Frame(20);
        }

        private void Set(int x, int y, int z, string material)
        {
            _world.LoadSource("test_set_" + ++_probes, "local terrain = workspace.Terrain "
                                                       + "terrain:FillBlock(CFrame.new(terrain:CellCenterToWorld(" + x + ", " + y
                                                       + ", " + z + ")), Vector3.new(4, 4, 4), Enum.Material." + material + ")");
        }

        private string At(int x, int y, int z)
        {
            return TerrainMaterials.GetName(_world.Material(x, y, z));
        }

        [Test]
        public void P6_Falling_SandWhoseSupportIsBroken_FallsOntoTheNextBlock()
        {
            StartOnFloor();
            Set(1, 1, 1, "Rock");
            Set(1, 2, 1, "Sand");
            _world.Frame(30);
            Assert.AreEqual("Sand", At(1, 2, 1), "Sand on a block stays");

            Set(1, 1, 1, "Air");
            _world.Frame(60);

            Assert.AreEqual("Sand", At(1, 1, 1), "the Sand fell into the broken block's cell");
            Assert.AreEqual("Air", At(1, 2, 1), "and left its own");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Falling_SandPlacedInTheAir_FallsToTheGround_ABlockATenthOfASecond()
        {
            StartOnFloor();
            Set(3, 6, 3, "Sand");
            _world.Frame(20);
            int rowAfterAThird = -99;
            for (int y = 1; y <= 6; y++)
            {
                rowAfterAThird = At(3, y, 3) == "Sand" ? y : rowAfterAThird;
            }

            _world.Frame(60);

            Assert.That(rowAfterAThird, Is.InRange(2, 5), "a third of a second in, the block is on its way down");
            Assert.AreEqual("Sand", At(3, 1, 3), "it rests on the ground");
            for (int y = 2; y <= 6; y++)
            {
                Assert.AreEqual("Air", At(3, y, 3), "nothing is left above it at row " + y);
            }

            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Falling_ARunOfFive_FallsTogether()
        {
            StartOnFloor();
            for (int y = 1; y <= 5; y++)
            {
                Set(5, y, 5, "Sand");
            }

            _world.Frame(20);
            Set(5, 0, 5, "Air");
            _world.Frame(60);

            for (int y = 0; y <= 4; y++)
            {
                Assert.AreEqual("Sand", At(5, y, 5), "the run moved down as one, row " + y);
            }

            Assert.AreEqual("Air", At(5, 5, 5), "its top cell is empty now");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Falling_SandSinksThroughWater_AndThePoolClosesOverIt()
        {
            StartOnFloor();
            for (int x = 2; x <= 4; x++)
            {
                for (int z = 2; z <= 4; z++)
                {
                    Set(x, -1, z, "Water");
                    Set(x, 0, z, "Water");
                }
            }

            Set(3, 3, 3, "Sand");
            _world.Frame(90);

            Assert.AreEqual("Sand", At(3, -1, 3), "the Sand rests on the pool's floor");
            Assert.AreEqual("Water", At(3, 0, 3), "the water closed over it");
            Assert.AreEqual("Air", At(3, 1, 3), "and no water rose above the pool");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Falling_AFallCutByAReload_CarriesOn_FromTheRunsTheModSaved()
        {
            StartOnFloor();
            Set(3, 12, 3, "Sand");
            _world.Frame(24);
            Assert.AreEqual("Air", At(3, 12, 3), "the fall has begun");
            Assert.AreEqual("Air", At(3, 1, 3), "and has not ended");

            // WHY the camera far away: only what the mod saved can carry the fall on, not a fresh look round the player.
            _world.LoadSource("test_camera_away", "workspace.CurrentCamera.CFrame = CFrame.new(4000, 14, 4000)");
            _world.Stack.Runtime.ReloadMod(Mod, BlockWorldTestWorld.Source(Mod));
            _world.Frame(150);

            Assert.AreEqual("Sand", At(3, 1, 3), "the reloaded mod finished the fall");
            _world.AssertHealthy(Mod);
        }

        [TestCase(400f, TestName = "P6_Falling_APlayerHighAboveTheLand_BreaksNothing")]
        [TestCase(-300f, TestName = "P6_Falling_APlayerDeepUnderTheLand_BreaksNothing")]
        public void P6_Falling_AWatcherOutsideTheWorldsRows_IsQuiet(float height)
        {
            StartOnFloor();
            _world.LoadSource("test_camera_far", "workspace.CurrentCamera.CFrame = CFrame.new(14, " + height + ", 14)");
            _world.Frame(60);

            Set(3, 6, 3, "Sand");
            _world.Frame(30);
            Assert.AreEqual("Sand", At(3, 6, 3), "nothing is watched from out there, so nothing moves");
            _world.AssertHealthy(Mod);
        }

        [TestCase("Grass")]
        [TestCase("Rock")]
        public void P6_Falling_GrassAndRock_NeverFall_NegativeTwin(string material)
        {
            StartOnFloor();
            Set(7, 4, 7, material);
            _world.Frame(60);

            Assert.AreEqual(material, At(7, 4, 7), material + " hangs where it was put");
            Assert.AreEqual("Air", At(7, 1, 7));
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Falling_AGeneratedWorld_HasNoSandOverAirOrWater()
        {
            using BlockWorldTestWorld world = new();
            world.SetAttribute("BlockWorldViewRadius", 2d);
            world.Load("blockworld_world");
            world.FrameUntil(() => world.Number("BlockWorldColumns") >= 21, 4000, "the radius-2 world");
            byte sand = BlockWorldTestWorld.Slot("Sand");
            int sandCells = 0;
            int hanging = 0;
            string example = "";
            for (int x = -48; x <= 47; x++)
            {
                for (int z = -48; z <= 47; z++)
                {
                    for (int y = -15; y <= 15; y++)
                    {
                        if (world.Material(x, y, z) != sand)
                        {
                            continue;
                        }

                        sandCells++;
                        byte under = world.Material(x, y - 1, z);
                        if (under == TerrainMaterials.Air || under == TerrainMaterials.Water)
                        {
                            hanging++;
                            example = example.Length == 0 ? "(" + x + ", " + y + ", " + z + ")" : example;
                        }
                    }
                }
            }

            TestContext.WriteLine("seed 1337, radius 2: " + sandCells + " Sand cells, " + hanging + " over air or water " + example);
            Assert.Greater(sandCells, 0, "the world has beaches");
            Assert.AreEqual(0, hanging, "a new world has nothing for the falling blocks to do: " + example);
            world.AssertHealthy("blockworld_world");
        }
    }
}
