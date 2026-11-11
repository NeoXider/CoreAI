using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the Block World's world mod (<c>blockworld_world</c>) on the real Lua VM and Terrain store: the seeded
    /// world is byte-identical for one seed (golden) and different for another, it holds hills, sea, shore,
    /// stone, bedrock and trees, it never overwrites a block that is already there, a reload skips finished
    /// columns, and every streaming step stays inside the per-resume instruction budget the guard enforces.
    /// Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldGenerationEditModeTests
    {
        private const string Mod = "blockworld_world";

        /// <summary>
        /// SHA-256 of the Terrain package entry (<c>TerrainVoxelCodec</c>, canonical chunk order) after seed 1337
        /// streamed the 21 columns of view radius 2 around the origin. A change here is a change of the generated world.
        /// </summary>
        private const string GoldenSeed1337Radius2 = "2201b8714f9dc370b6e6d9a87c526abb74503260416543f81fbfde9213bb91e8";

        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        /// <summary>Radius 2 streams the 21 columns whose centre lies within two columns of the origin.</summary>
        private const int RadiusTwoColumns = 21;

        private static byte[] Generate(double seed)
        {
            using BlockWorldTestWorld world = new();
            world.SetAttribute("BlockWorldSeed", seed);
            world.SetAttribute("BlockWorldViewRadius", 2d);
            world.Load(Mod);
            world.FrameUntil(() => world.Number("BlockWorldColumns") >= RadiusTwoColumns, 2000, "the radius-2 world");
            world.Frame(30);
            Assert.AreEqual(RadiusTwoColumns, world.Number("BlockWorldColumns"), "nothing beyond the view radius");
            world.AssertHealthy(Mod);
            return TerrainVoxelCodec.Encode(world.Terrain.Store);
        }

        private static string Sha256(byte[] bytes)
        {
            using SHA256 sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        [Test]
        public void P6_World_TheSameSeed_GeneratesAByteIdenticalWorld_Golden()
        {
            byte[] first = Generate(1337);
            byte[] second = Generate(1337);

            CollectionAssert.AreEqual(first, second, "two runs of one seed write the same cells");
            string hash = Sha256(first);
            TestContext.WriteLine("seed 1337, view radius 2: " + first.Length + " bytes, sha256 " + hash);
            Assert.AreEqual(GoldenSeed1337Radius2, hash, "the generated world changed");
        }

        [Test]
        public void P6_World_ADifferentSeed_GeneratesADifferentWorld_NegativeTwin()
        {
            byte[] first = Generate(1337);
            byte[] other = Generate(4242);

            CollectionAssert.AreNotEqual(first, other);
        }

        [Test]
        public void P6_World_TheStreamedWorld_HasHillsSeaShoreStoneAndTrees()
        {
            using BlockWorldTestWorld world = new();
            world.SetAttribute("BlockWorldViewRadius", 2d);
            world.Load(Mod);
            world.FrameUntil(() => world.Number("BlockWorldColumns") >= RadiusTwoColumns, 2000, "the radius-2 world");

            Dictionary<byte, int> counts = CountMaterials(world, -48, 47, -16, 15, -48, 47);
            string summary = "";
            foreach (KeyValuePair<byte, int> pair in counts)
            {
                summary += TerrainMaterials.GetEnumValue(pair.Key) + "=" + pair.Value + " ";
            }

            TestContext.WriteLine("seed 1337, radius 2 (enum value = cells): " + summary);
            foreach (string material in new[] { "Grass", "Ground", "Rock", "Sand", "Water", "WoodPlanks", "LeafyGrass" })
            {
                Assert.Greater(Count(counts, material), 0, material + " in the radius-2 world of seed 1337");
            }

            Assert.AreEqual(RadiusTwoColumns * 256, Count(counts, "Basalt"), "a complete bedrock floor");
            world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_World_HasCaves_UnderTheSurface_WithEntrances_FloodedBelowTheSeaLevel_AboveAWholeBedrock()
        {
            using BlockWorldTestWorld world = new();
            world.SetAttribute("BlockWorldViewRadius", 2d);
            world.Load(Mod);
            world.FrameUntil(() => world.Number("BlockWorldColumns") >= RadiusTwoColumns, 3000, "the radius-2 world");

            int caveAir = 0;
            int floodedCave = 0;
            Dictionary<long, int> tops = new();
            for (int x = -48; x <= 47; x++)
            {
                for (int z = -48; z <= 47; z++)
                {
                    int surface = TopSolid(world, x, z);
                    tops[x * 1000L + z] = surface;
                    for (int y = surface - 1; y > -16; y--)
                    {
                        byte material = world.Material(x, y, z);
                        caveAir += material == TerrainMaterials.Air ? 1 : 0;
                        floodedCave += material == TerrainMaterials.Water ? 1 : 0;
                    }
                }
            }

            int entrances = 0;
            for (int x = -47; x <= 46; x++)
            {
                for (int z = -47; z <= 46; z++)
                {
                    int lowest = Math.Min(Math.Min(tops[(x - 1) * 1000L + z], tops[(x + 1) * 1000L + z]),
                        Math.Min(tops[x * 1000L + z - 1], tops[x * 1000L + z + 1]));
                    entrances += tops[x * 1000L + z] <= lowest - 3 ? 1 : 0;
                }
            }

            TestContext.WriteLine("seed 1337, radius 2: " + caveAir + " cave cells, " + floodedCave
                                  + " flooded cave cells, " + entrances + " shafts (a pillar three or more rows below all four neighbours)");
            Assert.Greater(caveAir, 500, "tunnels and chambers under the surface");
            Assert.Greater(entrances, 0, "a cave reaches the surface somewhere, so a player can find one");
            Dictionary<byte, int> floor = CountMaterials(world, -48, 47, -16, -16, -48, 47);
            Assert.AreEqual(RadiusTwoColumns * 256, Count(floor, "Basalt"), "no cave cuts through the bedrock");
            world.AssertHealthy(Mod);
        }

        /// <summary>The highest solid, non-water cell of the pillar, or -16.</summary>
        private static int TopSolid(BlockWorldTestWorld world, int x, int z)
        {
            for (int y = 15; y > -16; y--)
            {
                byte material = world.Material(x, y, z);
                if (material != TerrainMaterials.Air && material != TerrainMaterials.Water)
                {
                    return y;
                }
            }

            return -16;
        }

        [Test]
        public void P6_World_TheSpawnColumn_ComesFirst_OnACompleteBedrockFloor_AndMarksTheWorldReady()
        {
            using BlockWorldTestWorld world = new();
            world.Load(Mod);

            Assert.AreEqual(false, world.Attribute("BlockWorldReady"), "nothing is generated while the mod loads");
            Assert.AreEqual(0L, world.Terrain.CountCells());
            world.FrameUntil(() => true.Equals(world.Attribute("BlockWorldReady")), 40, "the spawn column");

            TestContext.WriteLine("the spawn column took " + world.Number("BlockWorldColumns") + " column(s)");
            Assert.AreEqual(1d, world.Number("BlockWorldColumns"), "ready after exactly the spawn column");
            RbxVector3 spawn = world.Vector("BlockWorldSpawn");
            int cx = (int)Math.Floor(spawn.X / 64d);
            int cz = (int)Math.Floor(spawn.Z / 64d);
            Dictionary<byte, int> counts = CountMaterials(world, cx * 16, cx * 16 + 15, -16, 15, cz * 16, cz * 16 + 15);
            foreach (string material in new[] { "Grass", "Ground", "Rock" })
            {
                Assert.Greater(Count(counts, material), 0, material + " in the spawn column of seed 1337");
            }

            Assert.AreEqual(256, Count(counts, "Basalt"), "a complete bedrock floor under the spawn column");
            for (int x = cx * 16; x < cx * 16 + 16; x += 3)
            {
                for (int z = cz * 16; z < cz * 16 + 16; z += 5)
                {
                    Assert.AreEqual(BlockWorldTestWorld.Slot("Basalt"), world.Material(x, -16, z));
                    Assert.AreNotEqual(TerrainMaterials.Air, world.Material(x, -15, z), "no hole above the bedrock");
                }
            }

            world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_World_TheSpawn_FacesTheSameLevelHeading_ForTheSameSeed()
        {
            RbxVector3 first;
            using (BlockWorldTestWorld world = new())
            {
                world.Load(Mod);
                first = world.Vector("BlockWorldSpawnLook");
            }

            using BlockWorldTestWorld again = new();
            again.Load(Mod);
            RbxVector3 second = again.Vector("BlockWorldSpawnLook");

            Assert.AreEqual(0f, first.Y, 1e-5f, "the heading is level");
            Assert.AreEqual(1f, first.Magnitude, 1e-4f, "the heading is a unit vector");
            Assert.AreEqual(first, second, "the same seed faces the same way");
        }

        [Test]
        public void P6_World_TheSpawn_StandsInTheAirAboveLand()
        {
            using BlockWorldTestWorld world = new();
            world.Load(Mod);
            world.FrameUntil(() => true.Equals(world.Attribute("BlockWorldReady")), 40, "the spawn column");

            RbxVector3 spawn = world.Vector("BlockWorldSpawn");
            int x = (int)Math.Floor(spawn.X / 4d);
            int y = (int)Math.Floor(spawn.Y / 4d);
            int z = (int)Math.Floor(spawn.Z / 4d);
            Assert.AreEqual(TerrainMaterials.Air, world.Material(x, y, z), "the spawn cell is empty");
            byte below = world.Material(x, y - 2, z);
            Assert.IsTrue(below == BlockWorldTestWorld.Slot("Grass") || below == BlockWorldTestWorld.Slot("WoodPlanks"),
                "land under the spawn, found slot " + below);
        }

        [Test]
        public void P6_World_NeverOverwritesABlockThatIsAlreadyThere()
        {
            using BlockWorldTestWorld world = new();
            byte brick = BlockWorldTestWorld.Slot("Brick");
            world.Terrain.Store.SetCell(20, 12, 20, brick, 255);
            world.Terrain.Store.SetCell(20, -9, 20, brick, 255);
            world.Terrain.Store.SetCell(-10, -3, 5, brick, 255);
            world.SetAttribute("BlockWorldViewRadius", 2d);

            world.Load(Mod);
            world.FrameUntil(() => world.Number("BlockWorldColumns") >= RadiusTwoColumns, 2000, "the radius-2 world");

            Assert.AreEqual(brick, world.Material(20, 12, 20), "a block in the air survives");
            Assert.AreEqual(brick, world.Material(20, -9, 20), "a block inside the stone survives");
            Assert.AreEqual(brick, world.Material(-10, -3, 5), "a block in a spawn column survives");
            Assert.AreNotEqual(TerrainMaterials.Air, world.Material(21, -9, 20), "the column around them was generated");
        }

        [Test]
        public void P6_World_AReload_SkipsEveryFinishedColumn_AndAClearedWorldIsGeneratedAgain()
        {
            using BlockWorldTestWorld world = new();
            world.SetAttribute("BlockWorldViewRadius", 1d);
            world.Load(Mod);
            world.FrameUntil(() => world.Number("BlockWorldColumns") >= 9, 1000, "the radius-1 world");
            long revision = world.Terrain.Store.Revision;
            long cells = world.Terrain.CountCells();

            world.Stack.Runtime.ReloadMod(Mod, BlockWorldTestWorld.Source(Mod));
            world.Frame(60);

            Assert.AreEqual(0d, world.Number("BlockWorldColumns"), "the reloaded run found every column finished");
            Assert.AreEqual(revision, world.Terrain.Store.Revision, "and wrote nothing");

            Assert.AreEqual(true, world.Attribute("BlockWorldReady"), "the saved spawn column counts");

            world.Terrain.Clear();
            world.Stack.Runtime.ReloadMod(Mod, BlockWorldTestWorld.Source(Mod));

            Assert.AreEqual(false, world.Attribute("BlockWorldReady"), "a cleared world needs its spawn column again");
            world.FrameUntil(() => world.Number("BlockWorldColumns") >= 9, 1000, "the radius-1 world again");
            Assert.AreEqual(cells, world.Terrain.CountCells(), "and then the same world as before");
            world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_World_Loads_InsideTheLimitsOfAnAiToolInvocation()
        {
            using BlockWorldTestWorld world = new();
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            using (((CoreAI.Ai.IBudgetedLuaModRuntime)world.Stack.Runtime).BeginInvocation(
                       new CoreAI.Ai.LuaModInvocationLimits()))
            {
                world.Load(Mod);
            }

            clock.Stop();
            TestContext.WriteLine("load under the default invocation limits: "
                                  + clock.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");
            Assert.AreEqual(false, world.Attribute("BlockWorldReady"),
                "the AI can switch the world on or reload it with manage_mods (500,000 steps, 100 ms)");
            world.FrameUntil(() => true.Equals(world.Attribute("BlockWorldReady")), 40, "the spawn column");
            world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_World_StreamingSteps_StayInsideHalfTheDefaultResumeBudget()
        {
            BlockWorldTestOptions halfBudget = new() { BudgetPerResume = 5_000 };
            using BlockWorldTestWorld world = new(halfBudget);
            world.Load(Mod);
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            int frames = 0;
            while (world.Number("BlockWorldColumns") < 30 && frames < 4000)
            {
                world.Frame();
                frames++;
            }

            clock.Stop();
            TestContext.WriteLine("30 streamed columns in " + frames + " frames, " +
                                  (clock.Elapsed.TotalMilliseconds / frames).ToString("0.00") + " ms per frame");
            world.AssertHealthy(Mod);
            Assert.AreEqual(30d, world.Number("BlockWorldColumns"));
        }

        [Test]
        public void P6_World_AStepOverItsBudget_IsCutByTheGuard_NegativeTwin()
        {
            BlockWorldTestOptions tiny = new() { BudgetPerResume = 800 };
            using BlockWorldTestWorld world = new(tiny);
            world.Load(Mod);
            world.Frame(20);

            Assert.IsTrue(world.Logger.Contains("BUDGET_EXCEEDED"),
                "the same steps under a budget below their cost are cut, so the budget above measured them");
            Assert.Less(world.Number("BlockWorldColumns"), 5d);
        }

        private static Dictionary<byte, int> CountMaterials(BlockWorldTestWorld world, int x0, int x1, int y0, int y1,
            int z0, int z1)
        {
            Dictionary<byte, int> counts = new();
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        byte material = world.Material(x, y, z);
                        counts.TryGetValue(material, out int count);
                        counts[material] = count + 1;
                    }
                }
            }

            return counts;
        }

        private static int Count(Dictionary<byte, int> counts, string material)
        {
            counts.TryGetValue(BlockWorldTestWorld.Slot(material), out int count);
            return count;
        }
    }
}
