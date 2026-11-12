using System.Threading;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.GuiLua
{
    /// <summary>
    /// The key that releases a focused <c>TextBox</c> (Enter, Escape) was the box's: <c>InputBegan</c> reports it
    /// with <c>gameProcessedEvent = true</c> in the step the release is seen, so a script that opens a chat on Enter
    /// does not reopen the box that Enter has just closed. The next key is the game's again.
    /// </summary>
    [TestFixture]
    public sealed class TextBoxClosingKeyLuaEditModeTests
    {
        private const string Mod = "box";
        private const int KeyReturn = 13;
        private const int KeyW = 119;

        private const string Source = @"
local UIS = game:GetService('UserInputService')
local player = game:GetService('Players'):GetPlayers()[1]
local gui = Instance.new('ScreenGui')
local box = Instance.new('TextBox')
box.Name = 'Box'
box.Parent = gui
gui.Parent = player.PlayerGui
UIS.InputBegan:Connect(function(input, gameProcessed)
  store_set(input.KeyCode.Name, tostring(gameProcessed))
end)";

        private SynchronizationContext _savedContext;
        private UxmlServiceTestWorld _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = UxmlServiceTestWorld.WithFake();
            _world.Roblox.Players.EnsureActor(_world.Roblox.Registry, "local-player");
            _world.Roblox.LocalPlayerActorId = "local-player";
            _world.Load(Mod, Source);
            _world.Frame();
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private RbxTextBox Box()
        {
            foreach (RbxInstance instance in _world.Roblox.Registry.GetLiveInstances())
            {
                if (instance is RbxTextBox box && box.Name == "Box")
                {
                    return box;
                }
            }

            return null;
        }

        [Test]
        public void TheEnterThatReleasedTheBox_IsProcessed_AndTheNextKeyIsNot()
        {
            RbxTextBox box = Box();
            box.CaptureFocus();
            _world.Frame();
            Assert.IsTrue(box.IsFocused);

            box.ReleaseFocus(true);
            _world.Input.PressKey(KeyReturn);
            _world.Frame();
            Assert.AreEqual("true", _world.Get(Mod, "Return"), "the box took the Enter that closed it");

            _world.Input.PressKey(KeyW);
            _world.Frame();
            Assert.AreEqual("false", _world.Get(Mod, "W"), "twin: the next key is the game's");
        }

        [Test]
        public void Negative_WithoutAFocusedBox_EnterIsTheGames()
        {
            _world.Input.PressKey(KeyReturn);
            _world.Frame();

            Assert.AreEqual("false", _world.Get(Mod, "Return"));
        }
    }
}
