using System.Threading;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the Block World's body and moves: a new character gets a block game's body from <c>blockworld_world</c>
    /// (1.8 blocks tall, a jump of 1.25 blocks, a walk of 4.3 blocks a second) and is placed by its feet; and
    /// <c>blockworld_build</c> sprints at 1.3 times the walk while Left Control is held or W was tapped twice, only while
    /// going forward and never in water.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldBodyEditModeTests
    {
        private const int KeyW = 119;
        private const int KeyLeftControl = 306;

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

        private void Probe(string code)
        {
            _world.LoadSource("test_body_" + ++_probes, code);
        }

        private double WalkSpeed()
        {
            Probe("workspace:SetAttribute('TestWalkSpeed', game:GetService('Players'):GetPlayers()[1].Character.Humanoid.WalkSpeed)");
            return _world.Number("TestWalkSpeed");
        }

        [Test]
        public void P6_Body_ANewCharacter_GetsABlockGamesBody_PlacedByItsFeet()
        {
            _world = new BlockWorldTestWorld();
            _world.SetAttribute("BlockWorldViewRadius", 2d);
            _world.ConnectLocalPlayer();
            _world.Load("blockworld_world");
            _world.Frame(2);

            Probe("local c = game:GetService('Players'):GetPlayers()[1].Character "
                  + "local h = c:FindFirstChildOfClass('Humanoid') "
                  + "local low = math.huge "
                  + "for _, p in ipairs(c:GetChildren()) do if p:IsA('BasePart') then low = math.min(low, p.Position.Y - p.Size.Y / 2) end end "
                  + "workspace:SetAttribute('TestScale', c:GetScale()) "
                  + "workspace:SetAttribute('TestJumpHeight', h.JumpHeight) "
                  + "workspace:SetAttribute('TestUseJumpPower', h.UseJumpPower) "
                  + "workspace:SetAttribute('TestWalk', h.WalkSpeed) "
                  + "workspace:SetAttribute('TestFeet', low)");
            RbxVector3 spawn = _world.Vector("BlockWorldSpawn");

            Assert.AreEqual(1.44d, _world.Number("TestScale"), 0.001d, "1.8 blocks tall: the 5-stud body at 1.44");
            Assert.AreEqual(5d, _world.Number("TestJumpHeight"), 0.001d, "a jump of 1.25 blocks");
            Assert.AreEqual(false, _world.Attribute("TestUseJumpPower"), "the jump is a height");
            Assert.AreEqual(17d, _world.Number("TestWalk"), 0.001d, "a walk of 4.3 blocks a second");
            Assert.AreEqual(spawn.Y - 3.5d, _world.Number("TestFeet"), 0.05d, "the feet half a stud over the spawn's ground");
            _world.AssertHealthy("blockworld_world");
        }

        private void StartWalking()
        {
            _world = new BlockWorldTestWorld();
            _world.ConnectLocalPlayer();
            _world.Load("blockworld_build");
            Probe("game:GetService('Players'):GetPlayers()[1].Character.Humanoid.WalkSpeed = 17");
            _world.Frame(2);
        }

        [Test]
        public void P6_Body_LeftControlWhileGoingForward_Sprints_AndLettingGoWalks()
        {
            StartWalking();
            _world.Input.PressKey(KeyW);
            _world.Input.PressKey(KeyLeftControl);
            _world.Frame(2);
            double sprinting = WalkSpeed();
            object shown = _world.Attribute("BlockWorldSprinting");
            _world.Input.ReleaseKey(KeyLeftControl);
            _world.Frame(2);
            double walking = WalkSpeed();
            _world.Input.ReleaseKey(KeyW);
            _world.Frame(2);

            Assert.AreEqual(22d, sprinting, 0.01d, "a sprint is 1.3 times the walk");
            Assert.AreEqual(true, shown, "and is published for the HUD");
            Assert.AreEqual(17d, walking, 0.01d, "letting go of Left Control walks again");
            _world.AssertHealthy("blockworld_build");
        }

        [Test]
        public void P6_Body_WTappedTwice_Sprints_UntilWIsLetGo()
        {
            StartWalking();
            _world.Tap(KeyW);
            _world.Input.PressKey(KeyW);
            _world.Frame(2);
            double sprinting = WalkSpeed();
            _world.Input.ReleaseKey(KeyW);
            _world.Frame(2);
            double after = WalkSpeed();

            Assert.AreEqual(22d, sprinting, 0.01d, "W twice sprints");
            Assert.AreEqual(17d, after, 0.01d, "and letting go of W ends it");
            _world.AssertHealthy("blockworld_build");
        }

        [Test]
        public void P6_Body_LeftControlStandingStill_OrInWater_DoesNotSprint_NegativeTwin()
        {
            StartWalking();
            _world.Input.PressKey(KeyLeftControl);
            _world.Frame(2);
            double standing = WalkSpeed();

            Probe("local root = game:GetService('Players'):GetPlayers()[1].Character.HumanoidRootPart "
                  + "workspace.Terrain:FillBlock(CFrame.new(workspace.Terrain:CellCenterToWorld("
                  + "workspace.Terrain:WorldToCell(root.Position).X, workspace.Terrain:WorldToCell(root.Position).Y, "
                  + "workspace.Terrain:WorldToCell(root.Position).Z)), Vector3.new(4, 4, 4), Enum.Material.Water)");
            _world.Input.PressKey(KeyW);
            _world.Frame(2);
            double swimming = WalkSpeed();

            Assert.AreEqual(17d, standing, 0.01d, "no sprint without going forward");
            Assert.AreEqual(17d, swimming, 0.01d, "no sprint in water");
            _world.AssertHealthy("blockworld_build");
        }
    }
}
