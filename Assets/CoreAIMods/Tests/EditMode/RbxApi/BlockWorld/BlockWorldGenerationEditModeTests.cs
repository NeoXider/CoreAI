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
        private const string GoldenSeed1337Radius2 = "3269e260ad5fd673252764c4c494b33f47a6f08a2d4122c37364aba5dcb2c49b";

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
            foreach (string material in new[] { "Grass", "Ground", "Rock", "Sand", "Water", "Mud", "LeafyGrass" })
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

        /// <summary>The radius-2 world of seed 1337, streamed to the end, and its spawn cell.</summary>
        private static BlockWorldTestWorld RadiusTwoWorld(out int spawnX, out int spawnZ)
        {
            BlockWorldTestWorld world = new();
            world.SetAttribute("BlockWorldViewRadius", 2d);
            world.Load(Mod);
            world.FrameUntil(() => world.Number("BlockWorldColumns") >= RadiusTwoColumns, 4000, "the radius-2 world");
            RbxVector3 spawn = world.Vector("BlockWorldSpawn");
            spawnX = (int)Math.Floor(spawn.X / 4f);
            spawnZ = (int)Math.Floor(spawn.Z / 4f);
            return world;
        }

        /// <summary>True when the pillar's column was generated (its bedrock row is laid last).</summary>
        private static bool Generated(BlockWorldTestWorld world, int x, int z)
        {
            return world.Material(x, -16, z) == BlockWorldTestWorld.Slot("Basalt");
        }

        /// <summary>The highest cell of the pillar that is ground (not air, water, trunk or leaves), or -16.</summary>
        private static int TopGround(BlockWorldTestWorld world, int x, int z)
        {
            byte trunk = BlockWorldTestWorld.Slot("Mud");
            byte leaves = BlockWorldTestWorld.Slot("LeafyGrass");
            for (int y = 15; y > -16; y--)
            {
                byte material = world.Material(x, y, z);
                if (material != TerrainMaterials.Air && material != TerrainMaterials.Water && material != trunk
                    && material != leaves)
                {
                    return y;
                }
            }

            return -16;
        }

        [Test]
        public void P6_World_HasRelief_ASnowyPeakNearTheSpawn_AndEveryPillarInsideItsRows()
        {
            using BlockWorldTestWorld world = RadiusTwoWorld(out int spawnX, out int spawnZ);
            byte snow = BlockWorldTestWorld.Slot("Snow");
            int highest = -16;
            int lowest = 16;
            int tallestAnything = -16;
            int snowyPeaks = 0;
            int nearestPeak = int.MaxValue;
            for (int x = -32; x <= 47; x++)
            {
                for (int z = -32; z <= 47; z++)
                {
                    if (!Generated(world, x, z))
                    {
                        continue;
                    }

                    int top = TopGround(world, x, z);
                    highest = Math.Max(highest, top);
                    lowest = Math.Min(lowest, top);
                    tallestAnything = Math.Max(tallestAnything, TopSolid(world, x, z));
                    if (top >= 12 && world.Material(x, top, z) == snow)
                    {
                        snowyPeaks++;
                        nearestPeak = Math.Min(nearestPeak, Math.Max(Math.Abs(x - spawnX), Math.Abs(z - spawnZ)));
                    }
                }
            }

            int spawnTop = TopGround(world, spawnX, spawnZ);
            TestContext.WriteLine("seed 1337, radius 2: ground from row " + lowest + " to row " + highest + ", the tallest "
                                  + "cell (a crown) at row " + tallestAnything + "; " + snowyPeaks + " snowy pillars, the "
                                  + "nearest " + nearestPeak + " cells from the spawn (spawn ground at row " + spawnTop + ")");
            Assert.LessOrEqual(highest, 13, "a peak keeps three rows above it inside the column");
            Assert.LessOrEqual(tallestAnything, 15, "nothing grows out of the column's rows");
            Assert.GreaterOrEqual(lowest, -12, "the deepest sea floor stays four rows over the bedrock");
            Assert.Greater(snowyPeaks, 0, "the first view's range has snow on its tops");
            Assert.LessOrEqual(nearestPeak, 40, "a peak rises within forty cells of the spawn");
            Assert.GreaterOrEqual(highest - spawnTop, 8, "eight rows or more above the spawn: a mountain, not a terrace");
            world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_World_TheLowlandIsGrass_TheShoreSand_AndOnlyHighOrSteepGroundIsBare_NegativeTwin()
        {
            using BlockWorldTestWorld world = RadiusTwoWorld(out _, out _);
            byte grass = BlockWorldTestWorld.Slot("Grass");
            byte sand = BlockWorldTestWorld.Slot("Sand");
            byte rock = BlockWorldTestWorld.Slot("Rock");
            byte snow = BlockWorldTestWorld.Slot("Snow");
            int lowlandGrass = 0;
            int lowlandBare = 0;
            int shoreSand = 0;
            for (int x = -32; x <= 47; x++)
            {
                for (int z = -32; z <= 47; z++)
                {
                    int top = TopGround(world, x, z);
                    byte material = world.Material(x, top, z);
                    if (top >= 1 && top <= 4)
                    {
                        lowlandGrass += material == grass ? 1 : 0;
                        lowlandBare += material == snow ? 1 : 0;
                    }
                    else if (top == -1)
                    {
                        shoreSand += material == sand ? 1 : 0;
                    }

                    if (top <= 4 && material == rock)
                    {
                        int lowestNeighbour = Math.Min(Math.Min(TopGround(world, x - 1, z), TopGround(world, x + 1, z)),
                            Math.Min(TopGround(world, x, z - 1), TopGround(world, x, z + 1)));
                        Assert.GreaterOrEqual(top - lowestNeighbour, 3, "low rock only on a face (" + x + ", " + z + ")");
                    }
                }
            }

            Assert.Greater(lowlandGrass, 500, "the lowland is grass");
            Assert.AreEqual(0, lowlandBare, "and never snow");
            Assert.Greater(shoreSand, 0, "the shore is sand");
            world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_World_GrowsAWoodNearTheSpawn_AndSingleTrees_TrunksApart_NoneAtTheSpawn()
        {
            using BlockWorldTestWorld world = RadiusTwoWorld(out int spawnX, out int spawnZ);
            byte trunk = BlockWorldTestWorld.Slot("Mud");
            List<(int X, int Z)> trunks = new();
            for (int x = -32; x <= 47; x++)
            {
                for (int z = -32; z <= 47; z++)
                {
                    int ground = TopGround(world, x, z);
                    if (ground < 15 && world.Material(x, ground + 1, z) == trunk)
                    {
                        trunks.Add((x, z));
                    }
                }
            }

            int nearSpawn = 0;
            int closest = int.MaxValue;
            for (int index = 0; index < trunks.Count; index++)
            {
                (int x, int z) = trunks[index];
                int fromSpawn = Math.Max(Math.Abs(x - spawnX), Math.Abs(z - spawnZ));
                nearSpawn += fromSpawn <= 24 ? 1 : 0;
                Assert.Greater(fromSpawn, 2, "no tree stands on the spawn");
                for (int other = index + 1; other < trunks.Count; other++)
                {
                    closest = Math.Min(closest, Math.Max(Math.Abs(x - trunks[other].X), Math.Abs(z - trunks[other].Z)));
                }
            }

            TestContext.WriteLine("seed 1337, radius 2: " + trunks.Count + " trees, " + nearSpawn
                                  + " within 24 cells of the spawn, trunks at least " + closest + " cells apart");
            Assert.Greater(trunks.Count, 80, "woods and single trees: far more than the three a column used to grow");
            Assert.GreaterOrEqual(nearSpawn, 12, "a wood within sight of the spawn");
            Assert.GreaterOrEqual(closest, 2, "no two trunks side by side");
            world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_World_ACaveMouthOpensOntoAHillsideNearTheSpawn_NoneInTheSpawnColumn()
        {
            using BlockWorldTestWorld world = RadiusTwoWorld(out int spawnX, out int spawnZ);
            int spawnColumnX = (int)Math.Floor(spawnX / 16d);
            int spawnColumnZ = (int)Math.Floor(spawnZ / 16d);
            int mouths = 0;
            int inSpawnColumn = 0;
            int nearest = int.MaxValue;
            for (int x = -31; x <= 46; x++)
            {
                for (int z = -31; z <= 46; z++)
                {
                    if (!IsHillsideMouth(world, x, z, out int _))
                    {
                        continue;
                    }

                    mouths++;
                    nearest = Math.Min(nearest, Math.Max(Math.Abs(x - spawnX), Math.Abs(z - spawnZ)));
                    if ((int)Math.Floor(x / 16d) == spawnColumnX && (int)Math.Floor(z / 16d) == spawnColumnZ)
                    {
                        inSpawnColumn++;
                    }
                }
            }

            TestContext.WriteLine("seed 1337, radius 2: " + mouths + " hillside mouth cells, the nearest " + nearest
                                  + " cells from the spawn");
            Assert.Greater(mouths, 0, "a cave opens onto a hillside, so a player sees a way in");
            Assert.LessOrEqual(nearest, 48, "near the spawn");
            Assert.AreEqual(0, inSpawnColumn, "the spawn's column keeps whole ground");
            world.AssertHealthy(Mod);
        }

        /// <summary>
        /// A cell two high and open to one side under a roof of rock: air at rows y and y + 1, ground at y - 1 and
        /// at y + 2 with the hill rising at least one row more above it, and the cell beside it open to the sky.
        /// </summary>
        private static bool IsHillsideMouth(BlockWorldTestWorld world, int x, int z, out int floor)
        {
            floor = 0;
            int top = TopGround(world, x, z);
            for (int y = top - 2; y >= top - 6 && y > -15; y--)
            {
                if (!Open(world, x, y, z) || !Open(world, x, y + 1, z) || Open(world, x, y + 2, z) || Open(world, x, y - 1, z)
                    || top < y + 3)
                {
                    continue;
                }

                foreach ((int dx, int dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    if (Open(world, x + dx, y, z + dz) && Open(world, x + dx, y + 1, z + dz)
                        && TopGround(world, x + dx, z + dz) < y)
                    {
                        floor = y;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Open(BlockWorldTestWorld world, int x, int y, int z)
        {
            return world.Material(x, y, z) == TerrainMaterials.Air;
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
        public void P6_World_ANewPlayer_IsHeld_UntilTheFirstViewIsBuilt_ThenLetGo()
        {
            using BlockWorldTestWorld world = new();
            world.ConnectLocalPlayer();
            world.Load(Mod);
            world.FrameUntil(() => true.Equals(world.Attribute("BlockWorldReady")), 40, "the spawn column");
            world.Frame(2);

            Assert.AreEqual(false, world.Attribute("BlockWorldOpened"), "the spawn column alone is not the first view");
            Assert.AreEqual(4d, world.Number("BlockWorldOpeningColumns"));
            Assert.IsTrue(RootAnchored(world, "test_held"), "the player waits at the spawn, held, behind the veil");

            world.FrameUntil(() => true.Equals(world.Attribute("BlockWorldOpened")), 600, "the first view");

            Assert.GreaterOrEqual(world.Number("BlockWorldColumns"), 4d, "the spawn column and the three it faces");
            Assert.IsFalse(RootAnchored(world, "test_let_go"), "then the player is let go");
            world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_World_AWorldWhoseSpawnColumnExists_HoldsNobody_NegativeTwin()
        {
            using BlockWorldTestWorld world = new();
            world.Load(Mod);
            world.FrameUntil(() => world.Number("BlockWorldColumns") >= 4d, 600, "four columns");

            world.Stack.Runtime.ReloadMod(Mod, BlockWorldTestWorld.Source(Mod));

            Assert.AreEqual(true, world.Attribute("BlockWorldOpened"), "a saved world opens at once: its land is there");
            world.AssertHealthy(Mod);
        }

        private static bool RootAnchored(BlockWorldTestWorld world, string probeId)
        {
            world.LoadSource(probeId, "workspace:SetAttribute('TestRootAnchored', "
                                      + "game:GetService('Players'):GetPlayers()[1].Character.HumanoidRootPart.Anchored)");
            return true.Equals(world.Attribute("TestRootAnchored"));
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
            Assert.IsTrue(below == BlockWorldTestWorld.Slot("Grass") || below == BlockWorldTestWorld.Slot("Mud"),
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
