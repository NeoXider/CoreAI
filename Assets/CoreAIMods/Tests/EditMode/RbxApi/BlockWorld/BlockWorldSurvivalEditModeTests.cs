using System.Threading;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the Block World's survival mod (<c>blockworld_survival</c>) on the Humanoid's own health, with a character
    /// motor the test lifts off the ground and sets down again: a fall of more than three blocks costs health in
    /// survival, a long one kills and the player comes back at the spawn, and the HUD's attributes follow; a fall into
    /// deep water costs nothing. Twins: a short fall costs nothing, and creative takes no damage at all.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldSurvivalEditModeTests
    {
        private const string Mod = "blockworld_survival";

        private SynchronizationContext _savedContext;
        private BlockWorldTestWorld _world;
        private RbxPlayer _player;
        private FallingMotor _motor;
        private int _moves;

        /// <summary>
        /// A motor whose ground contact and water the test switches; the root's height is set with PivotTo.
        /// </summary>
        private sealed class FallingMotor : IRbxCharacterMotor
        {
            public bool Grounded { get; set; } = true;

            public RbxWaterContact Water { get; set; } = RbxWaterContact.Dry;

            public bool SetSwimming(bool swimming)
            {
                return true;
            }

            public RbxVector3 Position => RbxVector3.Zero;

            public RbxVector3 MoveDirection => RbxVector3.Zero;

            public bool IsGrounded => Grounded;

            public void SetWalkSpeed(double studsPerSecond)
            {
            }

            public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
            }

            public void MoveTo(RbxVector3? targetStuds)
            {
            }
        }

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = new BlockWorldTestWorld();
            _motor = new FallingMotor();
            _world.Roblox.AttachCharacterMotorFactory(_ => _motor);
            _player = _world.ConnectLocalPlayer();
            _world.SetAttribute("BlockWorldCreative", false);
            _world.Load(Mod);
            _world.Frame(2);
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private RbxHumanoid Humanoid => (RbxHumanoid)_player.Character.FindFirstChildOfClass("Humanoid");

        private void PlaceAt(double height)
        {
            _moves++;
            _world.LoadSource("test_place_" + _moves,
                "game:GetService('Players'):GetPlayers()[1].Character:PivotTo(CFrame.new(2, " + height + ", 2))");
        }

        /// <summary>Stands at <paramref name="from"/>, leaves the ground, comes down at <paramref name="to"/> and lands.</summary>
        private void Fall(double from, double to)
        {
            PlaceAt(from);
            _world.Frame(2);
            _motor.Grounded = false;
            _world.Frame(3);
            PlaceAt(to);
            _world.Frame(2);
            _motor.Grounded = true;
            _world.Frame(3);
        }

        [Test]
        public void P6_Survival_AFallOfTenBlocks_CostsHealth_AndTheHudAttributesFollow()
        {
            Fall(100, 60);

            Assert.AreEqual(30d, Humanoid.Health, 0.01d, "40 studs: (40 - 12) x 2.5 = 70 health");
            Assert.AreEqual(30d, System.Convert.ToDouble(_world.Attribute("BlockWorldHealth")), 0.01d);
            Assert.AreEqual(100d, System.Convert.ToDouble(_world.Attribute("BlockWorldMaxHealth")), 0.01d);
            StringAssert.Contains("fall of 10 blocks", (string)_world.Attribute("BlockWorldNotice"));
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Survival_AFallIntoDeepWater_CostsNothing_EvenAfterClimbingOutOntoTheBank()
        {
            PlaceAt(100);
            _world.Frame(2);
            _motor.Grounded = false;
            _world.Frame(3);
            PlaceAt(60);
            _motor.Water = new RbxWaterContact(true, true, 70d);
            _world.Frame(3);
            object swimming = Humanoid.GetState();
            _motor.Water = RbxWaterContact.Dry;
            _motor.Grounded = true;
            _world.Frame(3);

            Assert.AreEqual(RbxHumanoidState.Swimming, swimming, "a fall into deep water swims");
            Assert.AreEqual(100d, Humanoid.Health, 0.01d, "water breaks the fall, also once the player climbs out");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Survival_AFallOfTwoBlocks_CostsNothing_NegativeTwin()
        {
            Fall(100, 92);

            Assert.AreEqual(100d, Humanoid.Health, 0.01d, "a fall within three blocks is free");
        }

        [Test]
        public void P6_Survival_InCreative_ALongFall_CostsNothing_NegativeTwin()
        {
            _world.SetAttribute("BlockWorldCreative", true);
            _world.Frame();

            Fall(100, 40);

            Assert.AreEqual(100d, Humanoid.Health, 0.01d, "creative switches the damage off");
        }

        [Test]
        public void P6_Survival_ALethalFall_Kills_SaysSo_AndThePlayerComesBack()
        {
            RbxInstance before = _player.Character;

            Fall(100, 30);

            Assert.AreEqual(0d, Humanoid.Health, 0.01d, "70 studs take more than 100 health");
            Assert.AreEqual(1d, System.Convert.ToDouble(_world.Attribute("BlockWorldDeaths")));
            StringAssert.StartsWith("You died", (string)_world.Attribute("BlockWorldNotice"));

            _world.Frame(240);

            Assert.IsNotNull(_player.Character, "a character again after the respawn time");
            Assert.AreNotSame(before, _player.Character, "a new character");
            Assert.AreEqual(100d, Humanoid.Health, 0.01d, "at full health");
            _world.AssertHealthy(Mod);
        }
    }
}
