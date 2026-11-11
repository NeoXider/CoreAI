using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Gui
{
    /// <summary>
    /// StarterGui for the solo case, engine-free: a spawn copies its children into the player's PlayerGui
    /// (owned by the same mod, so they go with it), a respawn replaces the copies, a ScreenGui with
    /// ResetOnSpawn false is copied once and kept, and a ScreenGui a script parented into PlayerGui itself
    /// survives a respawn (the recorded deviation). The core-GUI switches are recorded.
    /// </summary>
    [TestFixture]
    public sealed class RbxStarterGuiEditModeTests
    {
        private RbxGuiTestWorld _world;
        private RbxStarterGui _starterGui;
        private RbxStarterGuiReplicator _replicator;

        [SetUp]
        public void SetUp()
        {
            _world = new RbxGuiTestWorld();
            _starterGui = (RbxStarterGui)_world.Game.GetService("StarterGui");
            _replicator = new RbxStarterGuiReplicator(_world.Game);
            _replicator.Track(_world.Player);
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        private void Spawn()
        {
            RbxInstance character = _world.Registry.Create("Model");
            _world.Player.Character = character;
            _replicator.Poll();
        }

        [Test]
        public void MVP15_StarterGui_IsARealServiceInTheGameTree()
        {
            Assert.IsInstanceOf<RbxStarterGui>(_starterGui);
            Assert.AreSame(_starterGui, _world.Game.FindFirstChild("StarterGui"));
            Assert.IsTrue(_starterGui.IsA("BasePlayerGui"));
            Assert.IsTrue(_world.PlayerGui.IsA("BasePlayerGui"));
        }

        [Test]
        public void MVP15_ASpawn_CopiesStarterGuiIntoPlayerGui_KeepingTheOwnerMod()
        {
            RbxScreenGui hud = _world.Create<RbxScreenGui>("ScreenGui", _starterGui);
            hud.Name = "Hud";
            _world.Create<RbxTextLabel>("TextLabel", hud);
            Assert.IsNull(_world.PlayerGui.FindFirstChild("Hud"), "nothing is copied before a spawn");

            Spawn();

            RbxInstance copy = _world.PlayerGui.FindFirstChild("Hud");
            Assert.IsNotNull(copy);
            Assert.AreNotSame(hud, copy);
            Assert.IsNotNull(copy.FindFirstChildOfClass("TextLabel"));
            Assert.IsTrue(_world.Registry.TryGetRecord(copy.Id, out InstanceRecord record));
            Assert.AreEqual(RbxGuiTestWorld.Mod, record.OwnerModId, "the copy goes away with the mod");

            _replicator.Poll();
            Assert.AreEqual(1, _world.PlayerGui.GetChildren().Count, "twin: no new spawn, no new copy");
        }

        [Test]
        public void MVP15_ARespawn_ReplacesTheCopies_ButKeepsResetOnSpawnFalse()
        {
            RbxScreenGui resetting = _world.Create<RbxScreenGui>("ScreenGui", _starterGui);
            resetting.Name = "Resetting";
            RbxScreenGui kept = _world.Create<RbxScreenGui>("ScreenGui", _starterGui);
            kept.Name = "Kept";
            kept.SetResetOnSpawn(false);
            Spawn();
            RbxInstance firstResetting = _world.PlayerGui.FindFirstChild("Resetting");
            RbxInstance firstKept = _world.PlayerGui.FindFirstChild("Kept");

            Spawn();

            Assert.IsTrue(firstResetting.IsDestroyed, "a respawn replaces a ResetOnSpawn copy");
            Assert.IsNotNull(_world.PlayerGui.FindFirstChild("Resetting"));
            Assert.AreSame(firstKept, _world.PlayerGui.FindFirstChild("Kept"),
                "twin: a ResetOnSpawn false copy is placed once and kept");
            Assert.AreEqual(2, _world.PlayerGui.GetChildren().Count);
        }

        [Test]
        public void MVP15_ARespawn_LeavesAScreenGuiAScriptParentedIntoPlayerGui()
        {
            RbxScreenGui scripted = _world.Screen();
            Spawn();
            Spawn();
            Assert.IsFalse(scripted.IsDestroyed,
                "DEV: only StarterGui copies reset; a mod does not re-run to rebuild its own HUD");
        }

        [Test]
        public void MVP15_CoreGuiSwitches_AreRecorded()
        {
            Assert.IsTrue(_starterGui.GetCoreGuiEnabled(2));
            _starterGui.SetCoreGuiEnabled(RbxStarterGui.CoreGuiTypeAll, false);
            Assert.IsFalse(_starterGui.GetCoreGuiEnabled(2));
            Assert.IsFalse(_starterGui.GetCoreGuiEnabled(RbxStarterGui.CoreGuiTypeAll));
            _starterGui.SetCoreGuiEnabled(2, true);
            Assert.IsTrue(_starterGui.GetCoreGuiEnabled(2));
            Assert.IsFalse(_starterGui.GetCoreGuiEnabled(RbxStarterGui.CoreGuiTypeAll),
                "All is true only while every type is");
            Assert.Throws<RbxError>(() => _starterGui.SetCoreGuiEnabled(99, true));
        }
    }
}
