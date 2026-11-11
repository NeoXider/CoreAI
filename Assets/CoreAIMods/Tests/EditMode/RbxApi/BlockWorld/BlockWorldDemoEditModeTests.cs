using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the whole Block World: the seven mods of the <c>Samples/Block World</c> folder load together with the
    /// headers the seeder needs, run side by side without an error or a budget trip, and work through each other's
    /// attributes (the hotbar chooses, the crosshair targets, the build mod places); and the "build here" recipe the
    /// skill text teaches writes cells only inside its box at the crosshair.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldDemoEditModeTests
    {
        /// <summary>The demo's mods in load order (the folder's order).</summary>
        public static readonly string[] Mods =
        {
            "blockworld_world", "blockworld_build", "blockworld_hud", "blockworld_daynight", "blockworld_torches",
            "blockworld_villagers", "blockworld_autopilot"
        };

        /// <summary>
        /// The example of the skill paragraph for block worlds (handed to the skill owner verbatim): a 3 x 8 x 3
        /// hollow Cobblestone tower with a 5 x 5 WoodPlanks platform and four posts, built in cells at the crosshair.
        /// </summary>
        public const string WatchtowerExample = @"
local terrain = workspace.Terrain
local base = workspace:GetAttribute('CrosshairPlaceCell')
if typeof(base) ~= 'Vector3' then
  error('point the crosshair at a block first')
end
local function fill(x0, y0, z0, x1, y1, z1, material)
  local min = terrain:CellCornerToWorld(x0, y0, z0)
  local max = terrain:CellCornerToWorld(x1 + 1, y1 + 1, z1 + 1)
  terrain:FillRegion(Region3.new(min, max), 4, material)
end
local x, y, z = base.X, base.Y, base.Z
fill(x - 1, y, z - 1, x + 1, y + 7, z + 1, Enum.Material.Cobblestone)
fill(x, y + 1, z, x, y + 7, z, Enum.Material.Air)
fill(x - 2, y + 8, z - 2, x + 2, y + 8, z + 2, Enum.Material.WoodPlanks)
for _, corner in ipairs({ { -2, -2 }, { 2, -2 }, { -2, 2 }, { 2, 2 } }) do
  fill(x + corner[1], y + 9, z + corner[2], x + corner[1], y + 9, z + corner[2], Enum.Material.WoodPlanks)
end";

        private SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public void P6_Demo_EveryModHeader_IsOneFolderOfInactiveBundledSamples()
        {
            foreach (string mod in Mods)
            {
                LuaModHeader header = LuaModHeader.Parse(BlockWorldTestWorld.Source(mod), "fallback");
                Assert.AreEqual(mod, header.Id);
                Assert.AreEqual("Samples/Block World", header.Category, mod + " lives in the demo's folder");
                Assert.IsFalse(header.Active, mod + " ships switched off, like every sample");
                Assert.AreEqual("All", header.Capabilities, mod);
                StringAssert.StartsWith("Block World ", header.Name, mod);
                StringAssert.Contains("Block World demo", header.Description, mod);
            }
        }

        [Test]
        public void P6_Demo_TheSevenMods_RunTogether_WithoutAnErrorOrABudgetTrip()
        {
            using BlockWorldTestWorld world = new(new BlockWorldTestOptions
            {
                Ai = new CoreAiLuaAiOptions { AllowAgentsFromLua = false }
            });
            world.SetAttribute("BlockWorldViewRadius", 2d);
            Stopwatch load = Stopwatch.StartNew();
            foreach (string mod in Mods)
            {
                world.Load(mod);
            }

            load.Stop();
            Stopwatch frames = Stopwatch.StartNew();
            world.Frame(600);
            frames.Stop();
            TestContext.WriteLine("load " + load.Elapsed.TotalMilliseconds.ToString("0") + " ms, 600 frames "
                                  + (frames.Elapsed.TotalMilliseconds / 600d).ToString("0.00") + " ms each, "
                                  + world.Number("BlockWorldColumns") + " columns");

            world.AssertHealthy(Mods);
            Assert.AreEqual(21d, world.Number("BlockWorldColumns"), "the radius-2 world streamed in");
            Assert.IsNotNull(world.Ui.Get("blockworld_hud/hud"), "the HUD is up");
            Assert.IsNotNull(world.Workspace.FindFirstChild("Mira"), "the villagers stand by the spawn");
        }

        [Test]
        public void P6_Demo_TheHotbarCrosshairAndBuildMods_WorkThroughEachOthersAttributes()
        {
            using BlockWorldTestWorld world = new();
            foreach (string mod in new[] { "blockworld_world", "blockworld_build", "blockworld_hud" })
            {
                world.Load(mod);
            }

            world.FrameUntil(() => world.Number("BlockWorldColumns") >= 5, 400, "the area around the spawn");
            RbxVector3 spawn = world.Vector("BlockWorldSpawn");
            world.Aim(spawn, new RbxVector3(spawn.X, spawn.Y - 8f, spawn.Z + 6f));
            Assert.AreEqual(true, world.Attribute("CrosshairHasTarget"), "the ground in front of the spawn");
            RbxVector3 place = world.Vector("CrosshairPlaceCell");

            world.Tap(BlockWorldTestWorld.KeyOne + 5);
            world.Click(1);

            Assert.AreEqual(BlockWorldTestWorld.Slot("Brick"), world.Material((int)place.X, (int)place.Y, (int)place.Z),
                "slot 6 of the HUD is Brick, and the build mod placed it");
            Assert.AreEqual("Placed 1  Broken 0", world.Ui.Get("blockworld_hud/hud").Find("counter").Text);
            world.AssertHealthy("blockworld_world", "blockworld_build", "blockworld_hud");
        }

        [Test]
        public void P6_Demo_TheSkillsWatchtower_WritesCellsOnlyInsideItsBoxAtTheCrosshair()
        {
            using BlockWorldTestWorld world = new();
            world.Terrain.Store.FillBox(TerrainCellBox.FromMinMax(-12, -3, -12, 13, 1, 13),
                BlockWorldTestWorld.Slot("Grass"), 255);
            world.Load("blockworld_build");
            world.Aim(new RbxVector3(10, 14, 4), new RbxVector3(10, 4, 10));
            RbxVector3 place = world.Vector("CrosshairPlaceCell");
            Assert.AreEqual(new RbxVector3(2, 1, 2), place);
            byte[] before = TerrainVoxelCodec.Encode(world.Terrain.Store);
            Dictionary<(int, int, int), byte> earlier = Snapshot(world);

            world.LoadSource("ai_watchtower", WatchtowerExample);

            int changed = 0;
            foreach (KeyValuePair<(int, int, int), byte> cell in Snapshot(world))
            {
                earlier.TryGetValue(cell.Key, out byte old);
                if (old == cell.Value)
                {
                    continue;
                }

                changed++;
                (int x, int y, int z) = cell.Key;
                Assert.IsTrue(x >= 0 && x <= 4 && y >= 1 && y <= 10 && z >= 0 && z <= 4,
                    "a changed cell outside the tower's box: " + cell.Key);
            }

            Assert.AreEqual(3 * 8 * 3 - 7 + 25 + 4, changed, "walls, platform and posts");
            Assert.AreEqual(BlockWorldTestWorld.Slot("Cobblestone"), world.Material(1, 1, 1));
            Assert.AreEqual(TerrainMaterials.Air, world.Material(2, 2, 2), "hollow inside");
            Assert.AreEqual(BlockWorldTestWorld.Slot("WoodPlanks"), world.Material(0, 9, 0));
            CollectionAssert.AreNotEqual(before, TerrainVoxelCodec.Encode(world.Terrain.Store));
            world.AssertHealthy("blockworld_build", "ai_watchtower");
        }

        [Test]
        public void P6_Demo_TheSkillsWatchtower_WithNoTarget_RefusesAndChangesNothing_NegativeTwin()
        {
            using BlockWorldTestWorld world = new();
            long revision = world.Terrain.Store.Revision;

            System.Exception refusal = Assert.Catch(() => world.Stack.Runtime.LoadMod("ai_watchtower",
                WatchtowerExample, LuaCapabilities.All, persistToStore: false));

            StringAssert.Contains("point the crosshair at a block first", refusal.Message,
                "the AI's chunk fails with a reason it can repair, so its next turn asks the player to aim");
            Assert.AreEqual(revision, world.Terrain.Store.Revision);
        }

        private static Dictionary<(int, int, int), byte> Snapshot(BlockWorldTestWorld world)
        {
            Dictionary<(int, int, int), byte> cells = new();
            for (int x = -12; x <= 12; x++)
            {
                for (int y = -3; y <= 14; y++)
                {
                    for (int z = -12; z <= 12; z++)
                    {
                        cells[(x, y, z)] = world.Material(x, y, z);
                    }
                }
            }

            return cells;
        }
    }
}
