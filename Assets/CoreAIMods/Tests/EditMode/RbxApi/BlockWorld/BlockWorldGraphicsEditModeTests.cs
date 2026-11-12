using System.Threading;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the Block World's graphics settings (<c>blockworld_graphics</c>): O opens a panel of Roblox GUI objects and
    /// frees the mouse, O again or Done closes it, and nothing opens while the chat has the keyboard; its buttons write
    /// the view distance (which the world mod streams and the fog follows, up to the host's limit), the field of view
    /// (<c>Camera.FieldOfView</c>) and the sun's brightness (<c>Lighting.Brightness</c>) as workspace attributes and
    /// raise <c>CoreAIWorldEdits</c>; settings a kept world holds, or that a script or the AI sets, are applied the
    /// same way. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldGraphicsEditModeTests
    {
        private const string Mod = "blockworld_graphics";

        private SynchronizationContext _savedContext;
        private BlockWorldTestWorld _world;
        private int _probes;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = new BlockWorldTestWorld();
            _world.ConnectLocalPlayer();
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private void LoadWorld(double radius, params string[] more)
        {
            _world.SetAttribute("BlockWorldViewRadius", radius);
            _world.Load("blockworld_world");
            foreach (string mod in more)
            {
                _world.Load(mod);
            }

            _world.Load(Mod);
            _world.Frame();
        }

        private RbxLighting Lighting => (RbxLighting)_world.Roblox.Game.FindFirstChildOfClass("Lighting");

        private RbxScreenGui Panel()
        {
            RbxInstance player = _world.Roblox.Players.GetPlayers()[0];
            RbxScreenGui gui = (RbxScreenGui)player.FindFirstChildOfClass("PlayerGui").FindFirstChild("BlockWorldGraphics");
            Assert.IsNotNull(gui, "the panel lives in the local player's PlayerGui");
            return gui;
        }

        private T Find<T>(string row, string name) where T : RbxInstance
        {
            RbxInstance found = Panel().FindFirstChild(row, true)?.FindFirstChild(name);
            Assert.IsNotNull(found, row + "." + name);
            return (T)found;
        }

        private string Value(string row)
        {
            return Find<RbxTextLabel>(row, "Value").TextState.Text;
        }

        private void Click(RbxGuiObject button)
        {
            FakeRbxUiBackend.FakeElement element =
                _world.UiWorld.GuiPresenter.GetElementPort(button) as FakeRbxUiBackend.FakeElement;
            Assert.IsNotNull(element, button.Name + " is drawn");
            element.ClickByPlayer();
            _world.Frame();
        }

        private void Click(string row, string name)
        {
            Click(Find<RbxTextButton>(row, name));
        }

        /// <summary>Runs <paramref name="lua"/> once as a mod of its own and settles a frame.</summary>
        private void Run(string lua)
        {
            _probes++;
            _world.LoadSource("test_probe_" + _probes, lua);
            _world.Frame();
        }

        private double FieldOfView()
        {
            Run("workspace:SetAttribute('TestFov', workspace.CurrentCamera.FieldOfView)");
            return _world.Number("TestFov");
        }

        private string MouseBehavior()
        {
            Run("workspace:SetAttribute('TestMouse', tostring(game:GetService('UserInputService').MouseBehavior))");
            return (string)_world.Attribute("TestMouse");
        }

        [Test]
        public void P6_Graphics_O_OpensThePanel_FreesTheMouse_AndOAgainOrDoneClosesIt()
        {
            LoadWorld(2d);
            Run("game:GetService('UserInputService').MouseBehavior = Enum.MouseBehavior.LockCenter");
            Assert.IsFalse(Panel().Enabled, "the panel starts closed");

            _world.Tap(BlockWorldTestWorld.KeyO);

            Assert.IsTrue(Panel().Enabled, "O opens it");
            Assert.AreEqual("Enum.MouseBehavior.Default", MouseBehavior(), "and frees the mouse, so a button can be clicked");
            Assert.AreEqual("2 columns, 32 blocks", Value("ViewDistance"));
            Assert.AreEqual("70 degrees", Value("FieldOfView"));
            Assert.AreEqual("100 percent", Value("SunBrightness"));
            _world.Tap(BlockWorldTestWorld.KeyO);
            Assert.IsFalse(Panel().Enabled, "O again closes it");
            _world.Tap(BlockWorldTestWorld.KeyO);
            Click(Panel().FindFirstChild("Done", true) as RbxGuiObject);
            Assert.IsFalse(Panel().Enabled, "and so does Done");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Graphics_O_WhileTheChatHasTheKeyboard_OpensNothing_NegativeTwin()
        {
            LoadWorld(2d);
            _world.Focus.SetCaptured("chat", true);

            _world.Tap(BlockWorldTestWorld.KeyO);

            Assert.IsFalse(Panel().Enabled, "an O typed into the chat is a letter, not the settings");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Graphics_TheViewDistanceButtons_SetTheStreamedRadius_AndTheFogFollows()
        {
            LoadWorld(2d, "blockworld_daynight");
            _world.FrameUntil(() => _world.Number("BlockWorldColumns") >= 21d, 3000, "the radius-2 world");
            _world.Tap(BlockWorldTestWorld.KeyO);

            Click("ViewDistance", "More");

            Assert.AreEqual(3d, _world.Number("BlockWorldViewRadius"), "the choice is the world mod's knob");
            Assert.AreEqual(3d, _world.Number("BlockWorldStreamRadius"), "which streams it");
            Assert.AreEqual("3 columns, 48 blocks", Value("ViewDistance"));
            _world.FrameUntil(() => _world.Number("BlockWorldColumns") >= 37d, 4000, "the radius-3 world");
            _world.Frame(30);
            Assert.AreEqual(BlockWorldViewRadiusEditModeTests.Reach(3) - 6d, Lighting.FogEnd, 0.01d,
                "the fog is whole just short of the longer reach once its land is made");

            Click("ViewDistance", "Less");

            Assert.AreEqual(2d, _world.Number("BlockWorldStreamRadius"));
            Assert.AreEqual(BlockWorldViewRadiusEditModeTests.Reach(2) - 6d, Lighting.FogEnd, 0.01d,
                "and comes nearer at once for a shorter one");
            Click("ViewDistance", "Less");
            Assert.AreEqual(2d, _world.Number("BlockWorldViewRadius"), "two columns is the shortest the panel offers");
            Assert.AreEqual(2d, _world.Number("CoreAIWorldEdits"), "each choice tells a kept world to record itself");
            _world.AssertHealthy(Mod, "blockworld_world", "blockworld_daynight");
        }

        [Test]
        public void P6_Graphics_TheViewDistance_StopsAtTheHostsLimit_NegativeTwin()
        {
            _world.SetAttribute("BlockWorldMaxViewRadius", 3d);
            LoadWorld(3d);
            _world.Tap(BlockWorldTestWorld.KeyO);

            Click("ViewDistance", "More");

            Assert.AreEqual(3d, _world.Number("BlockWorldViewRadius"), "nothing past the host's limit is asked for");
            Assert.IsNull(_world.Attribute("CoreAIWorldEdits"), "and nothing is recorded");
            Assert.AreEqual(0.65d, Find<RbxTextButton>("ViewDistance", "More").TextState.TextTransparency, 0.001d,
                "the + is dimmed at the limit");
            Assert.AreEqual(0d, Find<RbxTextButton>("ViewDistance", "Less").TextState.TextTransparency, 0.001d);
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Graphics_TheFieldOfViewAndSunButtons_SetTheCameraAndTheSun()
        {
            LoadWorld(2d);
            _world.Tap(BlockWorldTestWorld.KeyO);

            Click("FieldOfView", "More");
            Click("SunBrightness", "Less");

            Assert.AreEqual(75d, _world.Number("BlockWorldFieldOfView"));
            Assert.AreEqual(75d, FieldOfView(), 0.01d, "Camera.FieldOfView follows");
            Assert.AreEqual("75 degrees", Value("FieldOfView"));
            Assert.AreEqual(0.75d, _world.Number("BlockWorldBrightness"));
            Assert.AreEqual(0.75f, Lighting.Brightness, 0.001f, "Lighting.Brightness follows");
            Assert.AreEqual("75 percent", Value("SunBrightness"));
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Graphics_TwoClicksInOneFrame_StepTwice_ThoughTheWorldModFollowsAFrameLater()
        {
            LoadWorld(2d);
            _world.Tap(BlockWorldTestWorld.KeyO);
            foreach (string row in new[] { "ViewDistance", "FieldOfView", "SunBrightness" })
            {
                FakeRbxUiBackend.FakeElement more = (FakeRbxUiBackend.FakeElement)_world.UiWorld.GuiPresenter
                    .GetElementPort(Find<RbxTextButton>(row, "More"));
                more.ClickByPlayer();
                more.ClickByPlayer();
            }

            _world.Frame();

            Assert.AreEqual(4d, _world.Number("BlockWorldViewRadius"));
            Assert.AreEqual(4d, _world.Number("BlockWorldStreamRadius"));
            Assert.AreEqual(80d, _world.Number("BlockWorldFieldOfView"));
            Assert.AreEqual(1.5d, _world.Number("BlockWorldBrightness"), 0.001d);
            Assert.AreEqual(6d, _world.Number("CoreAIWorldEdits"));
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Graphics_TheSettingsAKeptWorldHolds_AreAppliedWhenTheModLoads()
        {
            _world.SetAttribute("BlockWorldFieldOfView", 90d);
            _world.SetAttribute("BlockWorldBrightness", 1.5d);

            LoadWorld(3d);

            Assert.AreEqual(90d, FieldOfView(), 0.01d);
            Assert.AreEqual(1.5f, Lighting.Brightness, 0.001f);
            Assert.AreEqual(3d, _world.Number("BlockWorldStreamRadius"));
            _world.Tap(BlockWorldTestWorld.KeyO);
            Assert.AreEqual("3 columns, 48 blocks", Value("ViewDistance"));
            Assert.AreEqual("90 degrees", Value("FieldOfView"));
            Assert.AreEqual("150 percent", Value("SunBrightness"));
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Graphics_AScriptOrTheAI_ChangesASettingByItsAttribute_AndThePanelGoesOnFromIt()
        {
            LoadWorld(2d);
            _world.Tap(BlockWorldTestWorld.KeyO);

            _world.SetAttribute("BlockWorldFieldOfView", 100d);
            _world.SetAttribute("BlockWorldBrightness", 0.6d);
            _world.Frame();

            Assert.AreEqual(100d, FieldOfView(), 0.01d);
            Assert.AreEqual(0.6f, Lighting.Brightness, 0.001f);
            Assert.AreEqual("100 degrees", Value("FieldOfView"), "the open panel shows the new value");
            Click("SunBrightness", "More");
            Assert.AreEqual(0.75d, _world.Number("BlockWorldBrightness"), 0.001d,
                "a value off the panel's steps goes to the next step, not a step past it");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Graphics_OpeningAndClosingThePanel_ChangesAndRecordsNothing_NegativeTwin()
        {
            LoadWorld(2d);

            _world.Tap(BlockWorldTestWorld.KeyO);
            _world.Tap(BlockWorldTestWorld.KeyO);

            Assert.IsNull(_world.Attribute("CoreAIWorldEdits"));
            Assert.IsNull(_world.Attribute("BlockWorldFieldOfView"));
            Assert.IsNull(_world.Attribute("BlockWorldBrightness"));
            Assert.AreEqual(2d, _world.Number("BlockWorldViewRadius"));
            Assert.AreEqual(70d, FieldOfView(), 0.01d);
            Assert.AreEqual(1f, Lighting.Brightness, 0.001f);
            _world.AssertHealthy(Mod);
        }
    }
}
