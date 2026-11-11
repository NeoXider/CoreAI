using System;
using System.Reflection;
using System.Threading;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.RuntimeUi;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using CoreAI.UiFocus;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode.RbxApi.GuiUnity
{
    /// <summary>
    /// The Roblox GUI classes over the real runtime UI host in a real UI Toolkit panel: a Roblox HUD script and
    /// a UxmlService screen are two screens of one host, built by one element factory; the typed styles land
    /// as inline styles; after the panel's layout pass the drawn boxes equal AbsolutePosition and AbsoluteSize;
    /// a real button submit reaches its Lua handler; a focused TextBox reaches the shared UI focus, so typing
    /// is processed input; unloading the mod leaves the host with nothing. Unity only: it needs UI Toolkit.
    /// </summary>
    [TestFixture]
    public sealed class RbxGuiRuntimeUiHostEditModeTests
    {
        private const string Mod = "hud";

        /// <summary>The HUD the lead captures next to a UXML screen (the Unity verification step).</summary>
        public const string HudSource = @"
local player = game:GetService('Players'):GetPlayers()[1]
local gui = Instance.new('ScreenGui')
gui.Name = 'RobloxHud'
gui.DisplayOrder = 1
local bar = Instance.new('Frame')
bar.Name = 'Bar'
bar.Size = UDim2.fromOffset(360, 120)
bar.Position = UDim2.fromOffset(40, 40)
bar.BackgroundColor3 = Color3.fromRGB(24, 28, 38)
bar.BorderSizePixel = 0
bar.Parent = gui
Instance.new('UICorner').Parent = bar
local stroke = Instance.new('UIStroke')
stroke.Color = Color3.fromRGB(255, 196, 64)
stroke.Thickness = 2
stroke.Parent = bar
local score = Instance.new('TextLabel')
score.Name = 'Score'
score.Size = UDim2.new(1, -20, 0, 40)
score.Position = UDim2.fromOffset(10, 10)
score.BackgroundTransparency = 1
score.Text = 'Coins: 0'
score.TextColor3 = Color3.new(1, 1, 1)
score.TextSize = 28
score.Font = Enum.Font.GothamBold
score.TextXAlignment = Enum.TextXAlignment.Left
score.Parent = bar
local shop = Instance.new('TextButton')
shop.Name = 'Shop'
shop.Size = UDim2.fromOffset(120, 40)
shop.Position = UDim2.new(1, -130, 1, -50)
shop.BackgroundColor3 = Color3.fromRGB(255, 196, 64)
shop.BorderSizePixel = 0
shop.Text = 'Shop'
shop.TextSize = 20
shop.Parent = bar
local coins = 0
shop.MouseButton1Click:Connect(function()
  coins = coins + 1
  score.Text = 'Coins: ' .. coins
  store_set('coins', tostring(coins))
end)
local chat = Instance.new('TextBox')
chat.Name = 'Chat'
chat.Size = UDim2.fromOffset(200, 30)
chat.Position = UDim2.fromOffset(10, 60)
chat.PlaceholderText = 'Say something'
chat.Text = ''
chat.Parent = bar
gui.Parent = player.PlayerGui
game:GetService('UxmlService'):CreateScreen('menu',
  '<ui:VisualElement class=""cai-panel"" style=""position: absolute; left: 440px; top: 40px; width: 240px;"">'
  .. '<ui:Label class=""cai-h1"" text=""UXML menu"" /><ui:Button class=""cai-btn"" text=""Play"" /></ui:VisualElement>',
  nil, { SortOrder = 0 })
";

        private SynchronizationContext _savedContext;
        private GameObject _panelObject;
        private PanelSettings _panelSettings;
        private UIDocument _document;
        private RuntimeUiHost _host;
        private CoreAiUiFocus _uiFocus;
        private UxmlServiceTestWorld _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _panelObject = new GameObject("RbxGuiRuntimeUiHostEditModeTests");
            _panelObject.SetActive(false);
            _document = _panelObject.AddComponent<UIDocument>();
            _document.panelSettings = _panelSettings;
            _panelObject.SetActive(true);
            _host = new RuntimeUiHost(new RuntimeUiHostOptions { AutoCreateSurface = false, PersistAcrossScenes = false });
            _host.AttachTo(_document.rootVisualElement);
            _uiFocus = new CoreAiUiFocus();
            _world = new UxmlServiceTestWorld(new RuntimeUiHostBackend(_host), uiFocus: _uiFocus);
            _world.Roblox.Players.EnsureActor(_world.Roblox.Registry, "local-player");
            _world.Roblox.LocalPlayerActorId = "local-player";
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _host?.Dispose();
            if (_panelObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_panelObject);
            }

            if (_panelSettings != null)
            {
                UnityEngine.Object.DestroyImmediate(_panelSettings);
            }

            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private RbxGuiPresenter Presenter => _world.World.GuiPresenter;

        private T Find<T>(string name) where T : RbxInstance
        {
            RbxPlayer player = ((RbxPlayers)_world.Roblox.Game.GetService("Players")).GetSoleListedPlayer();
            return (T)player.FindFirstChildOfClass("PlayerGui").FindFirstChild(name, true);
        }

        private RuntimeUiScreen GuiScreen()
        {
            for (int i = 0; i < _host.Screens.Count; i++)
            {
                if (_host.Screens[i].Id.StartsWith("rbxgui/", StringComparison.Ordinal))
                {
                    return _host.Screens[i];
                }
            }

            return null;
        }

        private VisualElement Drawn(RbxGuiObject gui)
        {
            IRbxUiElement port = Presenter.GetElementPort(gui);
            Assert.IsNotNull(port, gui.Name + " is drawn");
            RuntimeUiElement handle = RuntimeUiHostBackend.HandleOf(port);
            Assert.IsNotNull(handle, gui.Name + " is drawn by the real host");
            return handle.VisualElement;
        }

        private void PanelPasses()
        {
            IPanel panel = _document.rootVisualElement.panel;
            Assert.IsNotNull(panel, "The UIDocument has no panel in EditMode.");
            MethodInfo applyStyles = panel.GetType().GetMethod("ApplyStyles",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo validateLayout = panel.GetType().GetMethod("ValidateLayout",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (applyStyles == null || validateLayout == null)
            {
                Assert.Ignore("This Unity version exposes no ApplyStyles/ValidateLayout on its panel type.");
            }

            applyStyles.Invoke(panel, null);
            validateLayout.Invoke(panel, null);
        }

        [Test]
        public void MVP15_RealHost_ARobloxHudAndAUxmlScreen_AreTwoScreensOfOneHost()
        {
            _world.Load(Mod, HudSource);
            _world.Frame();

            Assert.AreEqual(2, _host.Screens.Count, "one ScreenGui and one UxmlService screen");
            RuntimeUiScreen gui = GuiScreen();
            Assert.IsNotNull(gui);
            Assert.AreEqual(Mod, gui.Owner, "both belong to the mod that made them");
            Assert.AreEqual(1, gui.SortOrder, "DisplayOrder is the host sort order");
            Assert.AreEqual(4, gui.ElementCount, "Bar, Score, Shop and Chat; UICorner and UIStroke draw on Bar");
            Assert.AreEqual(0, CountProblems(gui), "the GUI screen USS and typed styles raise no report: " + Describe(gui));

            VisualElement bar = Drawn(Find<RbxGuiObject>("Bar"));
            Assert.AreEqual(Position.Absolute, bar.style.position.value);
            Assert.AreEqual(38f, bar.style.left.value.value, 0.01f, "40px minus the 2px stroke outside the box");
            Assert.AreEqual(364f, bar.style.width.value.value, 0.01f);
            VisualElement score = Drawn(Find<RbxGuiObject>("Score"));
            Assert.IsInstanceOf<Label>(score, "a TextLabel is a factory Label");
            Assert.AreEqual("Coins: 0", ((Label)score).text);
            Assert.IsInstanceOf<Button>(Drawn(Find<RbxGuiObject>("Shop")));
            Assert.IsInstanceOf<TextField>(Drawn(Find<RbxGuiObject>("Chat")));
        }

        [Test]
        public void MVP15_RealHost_AfterTheLayoutPass_TheDrawnBoxesAreTheAbsoluteValues()
        {
            _world.Load(Mod, HudSource);
            _world.Frame();
            PanelPasses();
            _world.Frame();
            PanelPasses();

            // WHY an outline per object: a border Roblox draws outside the box (an Outer UIStroke, or a
            // BorderSizePixel border with BorderMode Outline) is the element's border, so the Roblox box is the
            // element's box inside it. Score keeps the TextLabel default BorderSizePixel = 1, invisible because
            // its BackgroundTransparency is 1, which is the one pixel between its element and its box.
            (string Name, float X, float Y, float W, float H, float Outline)[] expected =
            {
                ("Bar", 40f, 40f, 360f, 120f, 2f),
                ("Score", 50f, 50f, 340f, 40f, 1f),
                ("Shop", 270f, 110f, 120f, 40f, 0f)
            };
            Rect root = _document.rootVisualElement.worldBound;
            foreach ((string name, float x, float y, float w, float h, float outline) in expected)
            {
                RbxGuiObject gui = Find<RbxGuiObject>(name);
                VisualElement drawn = Drawn(gui);
                Rect box = drawn.worldBound;
                Assert.AreEqual(x, gui.AbsolutePosition.X, 0.01f, name + " AbsolutePosition.X");
                Assert.AreEqual(y, gui.AbsolutePosition.Y, 0.01f, name + " AbsolutePosition.Y");
                Assert.AreEqual(w, gui.AbsoluteSize.X, 0.01f, name + " AbsoluteSize.X");
                Assert.AreEqual(h, gui.AbsoluteSize.Y, 0.01f, name + " AbsoluteSize.Y");
                Assert.AreEqual(outline, drawn.resolvedStyle.borderLeftWidth, 0.01f, name + " border");
                Assert.AreEqual(outline, drawn.resolvedStyle.borderTopWidth, 0.01f, name + " border");
                Assert.AreEqual(gui.AbsolutePosition.X, box.x - root.x + outline, 0.5f, name + " x");
                Assert.AreEqual(gui.AbsolutePosition.Y, box.y - root.y + outline, 0.5f, name + " y");
                Assert.AreEqual(gui.AbsoluteSize.X, box.width - 2f * outline, 0.5f, name + " width");
                Assert.AreEqual(gui.AbsoluteSize.Y, box.height - 2f * outline, 0.5f, name + " height");
            }
        }

        [Test]
        public void MVP15_RealHost_AButtonSubmit_ReachesItsLuaHandler_AndUpdatesTheLabel()
        {
            _world.Load(Mod, HudSource);
            _world.Frame();

            VisualElement shop = Drawn(Find<RbxGuiObject>("Shop"));
            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = shop;
                shop.SendEvent(submit);
            }

            _world.Frame();
            _world.Frame();

            Assert.AreEqual("1", _world.Get(Mod, "coins"));
            Assert.AreEqual("Coins: 1", ((Label)Drawn(Find<RbxGuiObject>("Score"))).text);
        }

        [Test]
        public void MVP15_RealHost_AFocusedTextBox_IsProcessedInput()
        {
            _world.Load(Mod, HudSource);
            _world.Frame();
            Assert.IsFalse(_uiFocus.HasTextFocus);

            Find<RbxTextBox>("Chat").CaptureFocus();

            Assert.IsTrue(_host.IsTextInputFocused);
            Assert.IsTrue(_uiFocus.HasTextFocus, "typing in a TextBox does not move the character");
            Assert.IsTrue(Find<RbxTextBox>("Chat").IsFocused);
        }

        [Test]
        public void MVP15_RealHost_Unload_LeavesTheHostEmpty()
        {
            _world.Load(Mod, HudSource);
            _world.Frame();
            RuntimeUiScreen gui = GuiScreen();

            Assert.IsTrue(_world.Unload(Mod));
            _world.Frame();

            Assert.IsTrue(gui.IsDestroyed);
            Assert.AreEqual(0, _host.Screens.Count);
            Assert.AreEqual(0, gui.LiveSubscriberCount);
            Assert.AreEqual(0, Presenter.ElementCount);
        }

        private static int CountProblems(RuntimeUiScreen screen)
        {
            int problems = 0;
            for (int i = 0; i < screen.Diagnostics.Count; i++)
            {
                if (screen.Diagnostics[i].Severity != RuntimeUiDiagnosticSeverity.Info)
                {
                    problems++;
                }
            }

            return problems;
        }

        private static string Describe(RuntimeUiScreen screen)
        {
            string text = string.Empty;
            for (int i = 0; i < screen.Diagnostics.Count; i++)
            {
                text += screen.Diagnostics[i] + "\n";
            }

            return text;
        }
    }
}
