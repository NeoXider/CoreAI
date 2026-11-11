using System.Threading;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the Block World's autopilot mod (<c>blockworld_autopilot</c>) against the autopilot's attribute contract on
    /// the local Player, with the test standing in for the host side: the mod publishes the block world's goals and
    /// facts, P asks for the autopilot and ends it, and the goals only a block world knows are carried out in Lua -
    /// build puts a plank shelter with a doorway beside the character, gather breaks the wood within reach - only
    /// while the autopilot drives. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldAutopilotEditModeTests
    {
        private const string Mod = "blockworld_autopilot";

        private SynchronizationContext _savedContext;
        private BlockWorldTestWorld _world;
        private RbxPlayer _player;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = new BlockWorldTestWorld();
            _world.Terrain.Store.FillBox(TerrainCellBox.FromMinMax(-12, -3, -12, 13, 1, 13),
                BlockWorldTestWorld.Slot("Grass"), 255);
            _world.SetAttribute("BlockWorldSpawn", new CoreAI.Mods.Rbx.Datatypes.RbxVector3(2, 8, 2));
            _player = _world.ConnectLocalPlayer();
            _world.LoadSource("test_stand", "game.Players:GetPlayers()[1].Character:PivotTo(CFrame.new(2, 7, 2))");
            _world.Load(Mod);
            _world.Frame();
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        /// <summary>What the host's autopilot writes once it drives and has chosen <paramref name="goal"/>.</summary>
        private void HostDrives(string goal)
        {
            _player.SetAttribute("Autopilot", true);
            _player.SetAttribute("AutopilotGoal", goal);
            _world.Frame();
        }

        [Test]
        public void P6_Autopilot_PublishesTheBlockWorldsGoalsAndFacts_OnThePlayer()
        {
            string goals = (string)_player.GetAttribute("AutopilotGoals");
            foreach (string goal in new[] { "wander:", "follow:", "gather:", "build:", "home:", "idle:" })
            {
                StringAssert.Contains(goal, goals);
            }

            string state = (string)_player.GetAttribute("AutopilotState");
            StringAssert.StartsWith("It is day", state);
            StringAssert.Contains("The spawn is", state);
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Autopilot_P_AsksForTheAutopilot_AndPAgainEndsIt()
        {
            _world.Tap(BlockWorldTestWorld.KeyP);
            Assert.AreEqual(true, _player.GetAttribute("Autopilot"), "the request the host answers");
            Assert.AreEqual("on", _world.Attribute("BlockWorldAutopilot"));

            _world.Tap(BlockWorldTestWorld.KeyP);
            Assert.AreEqual(false, _player.GetAttribute("Autopilot"));
            Assert.AreEqual("", _world.Attribute("BlockWorldAutopilot"));
        }

        [Test]
        public void P6_Autopilot_PTypedIntoTheChat_AsksForNothing_NegativeTwin()
        {
            _world.Focus.SetCaptured("chat", true);
            _world.Tap(BlockWorldTestWorld.KeyP);

            Assert.IsNull(_player.GetAttribute("Autopilot"));
        }

        [Test]
        public void P6_Autopilot_Build_PutsAPlankShelterWithADoorway_BesideTheCharacter()
        {
            HostDrives("build");
            Assert.AreEqual("build", _world.Attribute("BlockWorldAutopilot"), "the banner shows the goal");

            _world.Frame(400);

            Assert.AreEqual(14d, _world.Number("BlockWorldAutopilotBuilt"), "two rows of seven planks");
            byte planks = BlockWorldTestWorld.Slot("WoodPlanks");
            int found = 0;
            for (int x = -1; x <= 1; x++)
            {
                for (int y = 1; y <= 2; y++)
                {
                    for (int z = -4; z <= -2; z++)
                    {
                        found += _world.Material(x, y, z) == planks ? 1 : 0;
                    }
                }
            }

            Assert.AreEqual(14, found, "all of it in the 3 x 2 x 3 box three cells ahead of the character");
            Assert.AreEqual(TerrainMaterials.Air, _world.Material(0, 1, -2), "the doorway faces the character");
            Assert.AreEqual(TerrainMaterials.Air, _world.Material(0, 1, -3), "and the inside is free");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Autopilot_EndingTheAutopilot_StopsTheBuild_NegativeTwin()
        {
            HostDrives("build");
            _world.Frame(60);
            double placed = _world.Number("BlockWorldAutopilotBuilt");
            Assert.Greater(placed, 0d);

            _player.SetAttribute("Autopilot", false);
            _world.Frame(120);

            Assert.AreEqual(placed, _world.Number("BlockWorldAutopilotBuilt"), "nothing more once the player has control");
            Assert.AreEqual("", _world.Attribute("BlockWorldAutopilot"));
        }

        [Test]
        public void P6_Autopilot_Gather_BreaksTheWoodWithinReach()
        {
            byte planks = BlockWorldTestWorld.Slot("WoodPlanks");
            _world.Terrain.Store.SetCell(2, 1, 0, planks, 255);

            HostDrives("gather");
            _world.Frame(30);

            Assert.AreEqual(TerrainMaterials.Air, _world.Material(2, 1, 0), "the planks were gathered");
            Assert.AreEqual(1d, _world.Number("BlockWorldAutopilotGathered"));
            Assert.AreEqual(BlockWorldTestWorld.Slot("Grass"), _world.Material(2, 0, 0), "grass is not a gather goal");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Autopilot_AGoalTheHostWalksFor_IsLeftToTheHost_NegativeTwin()
        {
            byte planks = BlockWorldTestWorld.Slot("WoodPlanks");
            _world.Terrain.Store.SetCell(2, 1, 0, planks, 255);
            long revision = _world.Terrain.Store.Revision;

            HostDrives("wander");
            _world.Frame(60);

            Assert.AreEqual(revision, _world.Terrain.Store.Revision, "wander moves the character, it changes no block");
            Assert.AreEqual("wander", _world.Attribute("BlockWorldAutopilot"));
        }

        [Test]
        public void P6_Autopilot_WhileOff_LeavesThePlayerUntouched_SoUndoStillApplies()
        {
            long before = PlayerRevision();

            _world.LoadSource("test_night", "game:GetService('Lighting').ClockTime = 22");
            _world.Frame(600);

            Assert.AreEqual(before, PlayerRevision(),
                "ten seconds with the autopilot off write nothing to the Player, which is part of the saved world");
            StringAssert.StartsWith("It is day", (string)_player.GetAttribute("AutopilotState"));
        }

        [Test]
        public void P6_Autopilot_WhileItDrives_KeepsTheFactsFresh_NegativeTwin()
        {
            HostDrives("idle");

            _world.LoadSource("test_night", "game:GetService('Lighting').ClockTime = 22");
            _world.Frame(180);

            StringAssert.StartsWith("It is night", (string)_player.GetAttribute("AutopilotState"),
                "the facts the model reads follow the world while the autopilot drives");
        }

        private long PlayerRevision()
        {
            Assert.IsTrue(_world.Roblox.Registry.TryGetRecord(_player.Id, out InstanceRecord record));
            return record.Revision;
        }
    }
}
