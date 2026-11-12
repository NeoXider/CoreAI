using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Mods.Rbx.Datatypes;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the Block World's view radius while the world runs (<c>blockworld_world</c>, the workspace attributes
    /// <c>BlockWorldViewRadius</c> and <c>BlockWorldMaxViewRadius</c>): a longer radius streams the new rings and moves
    /// <c>BlockWorldViewStuds</c> (the fog's reach) out only as they become whole, a shorter one keeps every column
    /// already made and brings the reach in at once, the radius is held to 1 up to 16 and to the host's limit, the
    /// reach is the nearest column left out for every radius, and at the longest radius every streaming step stays
    /// inside half the default resume budget. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldViewRadiusEditModeTests
    {
        private const string Mod = "blockworld_world";

        /// <summary>Studs a column is wide.</summary>
        private const double ColumnStuds = 64d;

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

        /// <summary>The columns (dx, dz) of a radius: dx * dx + dz * dz &lt;= r * r + 1.</summary>
        private static int ColumnsOf(int radius)
        {
            int count = 0;
            for (int dx = -radius - 1; dx <= radius + 1; dx++)
            {
                for (int dz = -radius - 1; dz <= radius + 1; dz++)
                {
                    count += dx * dx + dz * dz <= radius * radius + 1 ? 1 : 0;
                }
            }

            return count;
        }

        /// <summary>
        /// The rule restated: the distance, from the worst place in the player's column, to the nearest column the
        /// radius leaves out, in studs.
        /// </summary>
        internal static double Reach(int radius)
        {
            double nearest = double.MaxValue;
            for (int dx = -radius - 1; dx <= radius + 1; dx++)
            {
                for (int dz = -radius - 1; dz <= radius + 1; dz++)
                {
                    if (dx * dx + dz * dz > radius * radius + 1)
                    {
                        double gx = Math.Max(Math.Abs(dx) - 1, 0);
                        double gz = Math.Max(Math.Abs(dz) - 1, 0);
                        nearest = Math.Min(nearest, Math.Sqrt(gx * gx + gz * gz));
                    }
                }
            }

            return nearest * ColumnStuds;
        }

        private static BlockWorldTestWorld World(object radius, object limit = null, int budgetPerResume = 0)
        {
            BlockWorldTestWorld world = new(new BlockWorldTestOptions { BudgetPerResume = budgetPerResume });
            if (radius != null)
            {
                world.SetAttribute("BlockWorldViewRadius", radius);
            }

            if (limit != null)
            {
                world.SetAttribute("BlockWorldMaxViewRadius", limit);
            }

            return world;
        }

        private static void StreamTo(BlockWorldTestWorld world, int columns, int maxFrames)
        {
            world.FrameUntil(() => world.Number("BlockWorldColumns") >= columns, maxFrames, columns + " columns");
            world.Frame(30);
        }

        [Test]
        public void P6_ViewRadius_ALongerRadius_StreamsTheNewRings_AndTheReachMovesOutOnlyAsTheyBecomeWhole()
        {
            using BlockWorldTestWorld world = World(1d);
            world.Load(Mod);
            StreamTo(world, ColumnsOf(1), 1000);
            Assert.AreEqual(1d, world.Number("BlockWorldStreamRadius"));
            Assert.AreEqual(Reach(1), world.Number("BlockWorldViewStuds"), 0.01d);

            world.SetAttribute("BlockWorldViewRadius", 3d);
            world.Frame();

            Assert.AreEqual(3d, world.Number("BlockWorldStreamRadius"), "the radius in use follows the attribute at once");
            Assert.AreEqual(Reach(1), world.Number("BlockWorldViewStuds"), 0.01d,
                "but the reach waits for the new land, so the fog never shows where it ends");
            List<double> reaches = new();
            for (int frame = 0; frame < 4000 && world.Number("BlockWorldColumns") < ColumnsOf(3); frame++)
            {
                world.Frame();
                double reach = world.Number("BlockWorldViewStuds");
                if (reaches.Count == 0 || reaches[reaches.Count - 1] != reach)
                {
                    reaches.Add(reach);
                }
            }

            world.Frame(30);
            TestContext.WriteLine("radius 1 to 3: the reach went " + string.Join(", ", reaches.ConvertAll(r => r.ToString("0.0")))
                                  + " studs; " + world.Number("BlockWorldColumns") + " columns");
            Assert.AreEqual(ColumnsOf(3), world.Number("BlockWorldColumns"), "the two new rings streamed in");
            Assert.AreEqual(Reach(3), world.Number("BlockWorldViewStuds"), 0.01d, "and the reach is radius 3's");
            Assert.Greater(reaches.Count, 1, "the reach moved out ring by ring, not in one jump at the end");
            for (int index = 1; index < reaches.Count; index++)
            {
                Assert.Greater(reaches[index], reaches[index - 1], "the reach only moves out while a radius grows");
            }

            world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_ViewRadius_AShorterRadius_KeepsEveryColumnMade_BringsTheReachInAtOnce_AndMakesNoMore_NegativeTwin()
        {
            using BlockWorldTestWorld world = World(2d);
            world.Load(Mod);
            StreamTo(world, ColumnsOf(2), 3000);
            long revision = world.Terrain.Store.Revision;
            long cells = world.Terrain.CountCells();

            world.SetAttribute("BlockWorldViewRadius", 1d);
            world.Frame();

            Assert.AreEqual(1d, world.Number("BlockWorldStreamRadius"));
            Assert.AreEqual(Reach(1), world.Number("BlockWorldViewStuds"), 0.01d, "the fog comes nearer at once");
            world.Frame(120);
            Assert.AreEqual(revision, world.Terrain.Store.Revision, "a shorter radius writes nothing");
            Assert.AreEqual(cells, world.Terrain.CountCells(), "and removes no land: the columns past it are kept");

            world.SetAttribute("BlockWorldViewRadius", 2d);
            world.Frame(30);

            Assert.AreEqual(revision, world.Terrain.Store.Revision, "back to radius 2, the kept land needs no column made");
            Assert.AreEqual(Reach(2), world.Number("BlockWorldViewStuds"), 0.01d, "and its reach is back at once");
            Assert.AreEqual(ColumnsOf(2), world.Number("BlockWorldColumns"));
            world.AssertHealthy(Mod);
        }

        [TestCase(40d, null, 16d)]
        [TestCase(16d, null, 16d)]
        [TestCase(0d, null, 1d)]
        [TestCase(-3d, null, 1d)]
        [TestCase(5.7d, null, 5d)]
        [TestCase("far", null, 8d)]
        [TestCase(null, null, 8d)]
        [TestCase(16d, 12d, 12d)]
        [TestCase(null, 6d, 6d)]
        [TestCase(10d, 99d, 10d)]
        public void P6_ViewRadius_IsHeldToOneUpToSixteen_AndToTheHostsLimit(object radius, object limit, double expected)
        {
            using BlockWorldTestWorld world = World(radius, limit);
            world.Load(Mod);

            Assert.AreEqual(expected, world.Number("BlockWorldStreamRadius"));
            Assert.AreEqual(limit == null ? 16d : Math.Min(16d, Convert.ToDouble(limit)),
                world.Number("BlockWorldViewRadiusLimit"));
            Assert.AreEqual(Reach((int)expected), world.Number("BlockWorldViewStuds"), 0.01d);
            world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_ViewRadius_AHostLimitSetWhileTheWorldRuns_ShortensTheRadiusInUse()
        {
            using BlockWorldTestWorld world = World(8d);
            world.Load(Mod);

            world.SetAttribute("BlockWorldMaxViewRadius", 4d);
            world.Frame();

            Assert.AreEqual(4d, world.Number("BlockWorldStreamRadius"));
            Assert.AreEqual(4d, world.Number("BlockWorldViewRadiusLimit"));
            Assert.AreEqual(Reach(4), world.Number("BlockWorldViewStuds"), 0.01d);
            world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_ViewRadius_TheReach_IsTheNearestColumnLeftOut_ForEveryRadius()
        {
            using BlockWorldTestWorld world = World(16d);
            world.Load(Mod);
            List<string> reaches = new();
            for (int radius = 16; radius >= 1; radius--)
            {
                world.SetAttribute("BlockWorldViewRadius", (double)radius);
                world.Frame();
                reaches.Add(radius + ": " + world.Number("BlockWorldViewStuds").ToString("0.0"));
                Assert.AreEqual(Reach(radius), world.Number("BlockWorldViewStuds"), 0.01d, "radius " + radius);
            }

            TestContext.WriteLine("the land is sure to reach (studs) at radius " + string.Join(", ", reaches));
            world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_ViewRadius_AtTheLongestRadius_EveryStreamingStepStaysInsideHalfTheDefaultResumeBudget()
        {
            // WHY a world of marks: a column whose bedrock row is laid counts as made, so the world mod reads every
            // column of radius 16 (805) as it would a saved world's, and makes the three left without one. That is
            // the costliest search there is: every ring scanned, the outer ones 128 columns each.
            (int X, int Z)[] missing = { (16, 1), (-16, 0), (1, -16) };
            using BlockWorldTestWorld world = World(16d, null, 5_000);
            byte basalt = BlockWorldTestWorld.Slot("Basalt");
            int marked = 0;
            for (int cx = -17; cx <= 17; cx++)
            {
                for (int cz = -17; cz <= 17; cz++)
                {
                    if (cx * cx + cz * cz > 16 * 16 + 1 || Array.IndexOf(missing, (cx, cz)) >= 0)
                    {
                        continue;
                    }

                    world.Terrain.Store.SetCell(cx * 16, -16, cz * 16, basalt, 255);
                    marked++;
                }
            }

            world.LoadSource("test_look", "local camera = workspace.CurrentCamera "
                                          + "camera.CameraType = Enum.CameraType.Scriptable "
                                          + "camera.CFrame = CFrame.lookAt(Vector3.new(32, 40, 32), Vector3.new(600, 40, 32))");
            world.Load(Mod);
            Assert.AreEqual(true, world.Attribute("BlockWorldReady"), "the spawn's column is one of the marked ones");
            int frames = 0;
            while (world.Number("BlockWorldColumns") < missing.Length && frames < 8000)
            {
                world.Frame();
                frames++;
            }

            TestContext.WriteLine(marked + " marked columns read and " + world.Number("BlockWorldColumns")
                                  + " made in " + frames + " frames under a budget of 5,000 instructions a step");
            world.AssertHealthy(Mod, "test_look");
            Assert.IsFalse(world.Logger.Contains("BUDGET_EXCEEDED"), "no step went over its budget");
            Assert.AreEqual((double)missing.Length, world.Number("BlockWorldColumns"), "the unmarked columns were made");
            foreach ((int x, int z) in missing)
            {
                Assert.AreEqual(basalt, world.Material(x * 16 + 5, -16, z * 16 + 5), "column (" + x + ", " + z + ")");
            }
        }
    }
}
