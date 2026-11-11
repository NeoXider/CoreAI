using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.GuiLua
{
    /// <summary>
    /// Lua-tier conformance of the Roblox GUI classes through the real mod runtime and Lua bindings over the
    /// fake runtime UI host: a plain Roblox HUD script runs unchanged and draws through the host, the signals
    /// people use reach their handlers, property signals fire, unimplemented members are loud stubs naming
    /// MVP15, the <c>Ui</c> capability, the shared budgets, unload and reload, TweenService on GUI properties
    /// and StarterGui on spawn. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class RbxGuiLuaEditModeTests
    {
        private const string Mod = "m";

        private const string Hud = @"
local player = game:GetService('Players'):GetPlayers()[1]
local gui = Instance.new('ScreenGui')
gui.Name = 'Hud'
gui.DisplayOrder = 2
local frame = Instance.new('Frame')
frame.Name = 'Bar'
frame.Size = UDim2.new(0.5, 0, 0, 100)
frame.Position = UDim2.fromScale(0.5, 0)
frame.AnchorPoint = Vector2.new(0.5, 0)
frame.BackgroundColor3 = Color3.fromRGB(30, 30, 30)
frame.BackgroundTransparency = 0.2
frame.BorderSizePixel = 0
frame.Parent = gui
local label = Instance.new('TextLabel')
label.Name = 'Score'
label.Size = UDim2.new(1, 0, 0.5, 0)
label.BackgroundTransparency = 1
label.Text = 'Score: 0'
label.TextColor3 = Color3.new(1, 1, 1)
label.TextSize = 24
label.Font = Enum.Font.GothamBold
label.TextXAlignment = Enum.TextXAlignment.Left
label.Parent = frame
local button = Instance.new('TextButton')
button.Name = 'Shop'
button.Size = UDim2.fromOffset(120, 40)
button.Position = UDim2.new(0, 10, 1, -50)
button.Text = 'Shop'
button.Parent = frame
local box = Instance.new('TextBox')
box.Name = 'Chat'
box.PlaceholderText = 'Say something'
box.Parent = frame
local corner = Instance.new('UICorner')
corner.CornerRadius = UDim.new(0, 12)
corner.Parent = frame
gui.Parent = player.PlayerGui
";

        private SynchronizationContext _savedContext;
        private UxmlServiceTestWorld _world;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private RbxGuiPresenter Presenter => _world.World.GuiPresenter;

        /// <summary>A world with the player at this screen connected, as the play composition connects it.</summary>
        private void NewWorld(RbxUiBudgets budgets = null)
        {
            _world = UxmlServiceTestWorld.WithFake(budgets);
            _world.Roblox.Players.EnsureActor(_world.Roblox.Registry, "local-player");
            _world.Roblox.LocalPlayerActorId = "local-player";
        }

        private RbxPlayer LocalPlayer()
        {
            RbxPlayers players = (RbxPlayers)_world.Roblox.Game.GetService("Players");
            return players.GetSoleListedPlayer();
        }

        private T Find<T>(string name) where T : RbxInstance
        {
            return (T)LocalPlayer().FindFirstChildOfClass("PlayerGui").FindFirstChild(name, true);
        }

        private FakeRbxUiBackend.FakeElement Element(string name)
        {
            return Presenter.GetElementPort(Find<RbxGuiObject>(name)) as FakeRbxUiBackend.FakeElement;
        }

        [Test]
        public void MVP15_Lua_APlainRobloxHudScript_DrawsThroughTheRuntimeUiHost()
        {
            NewWorld();
            _world.Load(Mod, Hud + @"
local frame = gui.Bar
store_set('abs', tostring(frame.AbsoluteSize))
store_set('pos', tostring(frame.AbsolutePosition))
store_set('font', tostring(frame.Score.Font))
store_set('align', tostring(frame.Score.TextXAlignment))
store_set('isa', tostring(frame.Score:IsA('GuiObject')) .. ',' .. tostring(gui:IsA('LayerCollector'))
  .. ',' .. tostring(frame.Shop:IsA('GuiButton')))
store_set('size', tostring(frame.Size))
store_set('text', frame.Score.Text)");
            _world.Frame();

            Assert.AreEqual("640, 100", _world.Get(Mod, "abs"), "AbsoluteSize reads the real layout at once");
            Assert.AreEqual("320, 0", _world.Get(Mod, "pos"));
            Assert.AreEqual("Enum.Font.GothamBold", _world.Get(Mod, "font"));
            Assert.AreEqual("Enum.TextXAlignment.Left", _world.Get(Mod, "align"));
            Assert.AreEqual("true,true,true", _world.Get(Mod, "isa"));
            Assert.AreEqual("Score: 0", _world.Get(Mod, "text"));

            RbxScreenGui hud = Find<RbxScreenGui>("Hud");
            FakeRbxUiBackend.FakeScreen host = Presenter.GetScreenPort(hud) as FakeRbxUiBackend.FakeScreen;
            Assert.IsNotNull(host);
            Assert.AreEqual(Mod, host.Owner);
            Assert.AreEqual(2, host.SortOrder);
            Assert.AreEqual("Score: 0", Element("Score").Text);
            Assert.AreEqual("Label", Element("Score").Kind);
            Assert.AreEqual("Button", Element("Shop").Kind);
            Assert.AreEqual("TextField", Element("Chat").Kind);
            Assert.AreEqual("Say something", Element("Chat").Attributes["placeholder-text"]);
            Assert.AreEqual("12px", Element("Bar").Typed(RbxUiStyleProperty.BorderTopLeftRadius).ToString());
            Assert.AreEqual(4, Presenter.ElementCount);
        }

        [Test]
        public void MVP15_Lua_TheSignalsPeopleUse_ReachTheirHandlers()
        {
            NewWorld();
            _world.Load(Mod, Hud + @"
local clicks, activations, entered = 0, 0, 0
frame.Shop.MouseButton1Click:Connect(function() clicks = clicks + 1; store_set('clicks', tostring(clicks)) end)
frame.Shop.Activated:Connect(function(input, count)
  activations = activations + 1
  store_set('activated', tostring(activations) .. ':' .. tostring(count) .. ':' .. tostring(input.UserInputType))
end)
frame.MouseEnter:Connect(function(x, y) entered = entered + 1; store_set('enter', x .. ',' .. y) end)
frame.Chat.FocusLost:Connect(function(enterPressed) store_set('lost', tostring(enterPressed) .. ':' .. frame.Chat.Text) end)
frame.Chat.Focused:Connect(function() store_set('focused', 'yes') end)
frame.Chat:GetPropertyChangedSignal('Text'):Connect(function() store_set('typed', frame.Chat.Text) end)");
            _world.Frame();

            Element("Shop").ClickByPlayer();
            Element("Bar").EnterByPlayer();
            Find<RbxTextBox>("Chat").CaptureFocus();
            Element("Chat").ChangeValueByPlayer(RbxUiValue.FromText("hi"));
            Element("Chat").SubmitByPlayer();
            _world.Frame();

            Assert.AreEqual("1", _world.Get(Mod, "clicks"));
            Assert.AreEqual("1:1:Enum.UserInputType.MouseButton1", _world.Get(Mod, "activated"));
            Assert.AreEqual("0,0", _world.Get(Mod, "enter"));
            Assert.AreEqual("yes", _world.Get(Mod, "focused"));
            Assert.AreEqual("hi", _world.Get(Mod, "typed"));
            Assert.AreEqual("true:hi", _world.Get(Mod, "lost"), "FocusLost(enterPressed) after Enter");
            Assert.AreEqual("Position", Element("Bar").Attributes["picking-mode"],
                "a Frame a script hovers takes the pointer");
        }

        [Test]
        public void MVP15_Lua_GetPropertyChangedSignal_FiresForGuiProperties_AndRefusesEvents()
        {
            NewWorld();
            _world.Load(Mod, Hud + @"
frame:GetPropertyChangedSignal('BackgroundTransparency'):Connect(function()
  store_set('bt', tostring(frame.BackgroundTransparency))
end)
frame:GetPropertyChangedSignal('AbsoluteSize'):Connect(function()
  store_set('abs', tostring(frame.AbsoluteSize))
end)
local ok, err = pcall(function() return frame.Shop:GetPropertyChangedSignal('MouseButton1Click') end)
store_set('event', tostring(ok) .. '|' .. tostring(err))
local ok2, err2 = pcall(function() return frame:GetPropertyChangedSignal('BackgroundTransparancy') end)
store_set('typo', tostring(ok2) .. '|' .. tostring(err2))
frame.BackgroundTransparency = 0.5");
            _world.Frame();
            RbxScreenGui hud = (RbxScreenGui)LocalPlayer().FindFirstChildOfClass("PlayerGui").FindFirstChild("Hud");
            ((FakeRbxUiBackend.FakeScreen)Presenter.GetScreenPort(hud)).Resize(800f, 600f);
            _world.Frame();
            _world.Frame();

            Assert.AreEqual("0.5", _world.Get(Mod, "bt"));
            Assert.AreEqual("400, 100", _world.Get(Mod, "abs"), "a resize reaches AbsoluteSize and its signal");
            StringAssert.StartsWith("false|", _world.Get(Mod, "event"), "an event is not a property");
            StringAssert.Contains("did you mean \"BackgroundTransparency\"", _world.Get(Mod, "typo"));
        }

        [Test]
        public void MVP15_Lua_UnimplementedGuiMembers_AreLoudStubsNamingTheirRung()
        {
            NewWorld();
            _world.Load(Mod, Hud + @"
local function try(key, fn) local ok, err = pcall(fn) store_set(key, tostring(ok) .. '|' .. tostring(err)) end
try('input', function() return frame.InputBegan end)
try('tween', function() frame:TweenPosition(UDim2.new(), 'Out', 'Quad', 1) end)
try('bounds', function() return frame.Score.TextBounds end)
try('setcore', function() game:GetService('StarterGui'):SetCore('SendNotification', {}) end)
try('gradient', function() Instance.new('UIGradient') end)
try('readonly', function() frame.AbsoluteSize = Vector2.new(1, 1) end)");

            StringAssert.Contains("planned for MVP15", _world.Get(Mod, "input"));
            StringAssert.Contains("planned for MVP15", _world.Get(Mod, "tween"));
            StringAssert.Contains("planned for MVP15", _world.Get(Mod, "bounds"));
            StringAssert.Contains("NOT_IMPLEMENTED", _world.Get(Mod, "setcore"));
            StringAssert.Contains("planned for MVP15", _world.Get(Mod, "gradient"));
            StringAssert.Contains("read only", _world.Get(Mod, "readonly"));
        }

        [Test]
        public void MVP15_Lua_CreatingGui_NeedsTheUiCapability()
        {
            NewWorld();
            _world.Load("noui", @"
local ok, err = pcall(function() return Instance.new('Frame') end)
store_set('r', tostring(ok) .. '|' .. tostring(err))
local ok2 = pcall(function() return Instance.new('Folder') end)
store_set('folder', tostring(ok2))", LuaCapabilities.All & ~LuaCapabilities.Ui);
            _world.Load("ui", @"
local ok = pcall(function() return Instance.new('Frame') end)
store_set('r', tostring(ok))");

            StringAssert.Contains(RbxUiErrors.CapabilityRequired, _world.Get("noui", "r"));
            Assert.AreEqual("true", _world.Get("noui", "folder"), "only GUI classes need Ui");
            Assert.AreEqual("true", _world.Get("ui", "r"), "twin: granted, it is created");
        }

        [Test]
        public void MVP15_Lua_UnloadRemovesTheGui_AndAReloadRebuildsIt()
        {
            NewWorld();
            _world.Load(Mod, Hud);
            _world.Frame();
            Assert.AreEqual(1, Presenter.ScreenCount);
            FakeRbxUiBackend.FakeScreen first = _world.Fake.Created[_world.Fake.Created.Count - 1];

            _world.Reload(Mod, Hud);
            _world.Frame();
            Assert.IsTrue(first.IsDestroyed, "the reloaded run's startup GUI replaces the old one");
            Assert.AreEqual(1, Presenter.ScreenCount);
            Assert.AreEqual(4, Presenter.ElementCount);

            Assert.IsTrue(_world.Unload(Mod));
            _world.Frame();
            Assert.AreEqual(0, Presenter.ScreenCount);
            Assert.AreEqual(0, Presenter.ElementCount);
            Assert.AreEqual(0, _world.World.CountScreens(Mod));
            Assert.AreEqual(0, _world.World.CountElements(Mod));
            Assert.AreEqual(0, _world.Fake.SubscriberCount, "no handler stays subscribed on the host");
        }

        [Test]
        public void MVP15_Lua_ScreenGuisAndUxmlScreens_ShareOneScreenBudget()
        {
            NewWorld(new RbxUiBudgets { MaxScreensPerMod = 1 });
            _world.Load(Mod, @"
game:GetService('UxmlService'):CreateScreen('hud', '<ui:Label text=""x"" />')
local gui = Instance.new('ScreenGui')
local ok, err = pcall(function() gui.Parent = game:GetService('Players'):GetPlayers()[1].PlayerGui end)
store_set('r', tostring(ok) .. '|' .. tostring(err))
store_set('parent', tostring(gui.Parent))");
            _world.Load("other", @"
local gui = Instance.new('ScreenGui')
local ok = pcall(function() gui.Parent = game:GetService('Players'):GetPlayers()[1].PlayerGui end)
store_set('r', tostring(ok))");

            StringAssert.Contains(RbxUiErrors.BudgetScreens, _world.Get(Mod, "r"));
            Assert.AreEqual("nil", _world.Get(Mod, "parent"), "the refused move did not happen");
            Assert.AreEqual("true", _world.Get("other", "r"), "twin: another mod has its own budget");
        }

        [Test]
        public void MVP15_Lua_TweenService_AnimatesGuiProperties()
        {
            NewWorld();
            _world.Load(Mod, Hud + @"
local tween = game:GetService('TweenService'):Create(frame, TweenInfo.new(0.1),
  { Position = UDim2.new(0, 0, 0, 0), BackgroundTransparency = 1, BackgroundColor3 = Color3.new(1, 0, 0) })
tween:Play()
tween.Completed:Connect(function()
  store_set('pos', tostring(frame.Position))
  store_set('bt', tostring(frame.BackgroundTransparency))
end)
local ok, err = pcall(function()
  game:GetService('TweenService'):Create(frame, TweenInfo.new(0.1), { Visible = false })
end)
store_set('bool', tostring(ok) .. '|' .. tostring(err))");
            for (int i = 0; i < 20; i++)
            {
                _world.Roblox.Scheduler.Advance(1d / 60d);
            }

            Assert.AreEqual("{0, 0}, {0, 0}", _world.Get(Mod, "pos"));
            Assert.AreEqual("1", _world.Get(Mod, "bt"));
            StringAssert.StartsWith("false|", _world.Get(Mod, "bool"), "a boolean is not tweenable, as in Roblox");
        }

        [Test]
        public void MVP15_Lua_StarterGui_IsCopiedIntoPlayerGuiWhenTheCharacterSpawns()
        {
            NewWorld();
            _world.Load(Mod, @"
local gui = Instance.new('ScreenGui')
gui.Name = 'FromStarter'
Instance.new('TextLabel').Parent = gui
gui.Parent = game:GetService('StarterGui')
local player = game:GetService('Players'):GetPlayers()[1]
store_set('before', tostring(player.PlayerGui:FindFirstChild('FromStarter')))");
            for (int i = 0; i < 3; i++)
            {
                _world.Frame();
            }

            Assert.AreEqual("nil", _world.Get(Mod, "before"), "nothing is copied before the character spawns");
            RbxInstance copy = LocalPlayer().FindFirstChildOfClass("PlayerGui").FindFirstChild("FromStarter");
            Assert.IsNotNull(copy, "the spawn copied StarterGui into PlayerGui");
            Assert.IsNotNull(Presenter.GetScreenPort((RbxScreenGui)copy), "and the copy is drawn");
            Assert.AreEqual(1, Presenter.ElementCount);
        }

        [Test]
        public void MVP15_Lua_EnumAndNumberProperties_RoundTripAndRefuseTheWrongType()
        {
            NewWorld();
            _world.Load(Mod, @"
local label = Instance.new('TextLabel')
label.TextYAlignment = 'Top'
label.ZIndex = 3.9
label.Text = 42
store_set('y', tostring(label.TextYAlignment))
store_set('z', tostring(label.ZIndex))
store_set('t', label.Text)
store_set('defaults', tostring(label.TextSize) .. '|' .. tostring(label.BorderSizePixel) .. '|'
  .. tostring(label.Size) .. '|' .. tostring(label.BackgroundColor3))
local ok, err = pcall(function() label.Size = Vector2.new(1, 1) end)
store_set('bad', tostring(ok) .. '|' .. tostring(err))
local ok2, err2 = pcall(function() label.TextXAlignment = Enum.TextYAlignment.Top end)
store_set('badenum', tostring(ok2) .. '|' .. tostring(err2))");

            Assert.AreEqual("Enum.TextYAlignment.Top", _world.Get(Mod, "y"));
            Assert.AreEqual("3", _world.Get(Mod, "z"));
            Assert.AreEqual("42", _world.Get(Mod, "t"));
            StringAssert.StartsWith("8|1|{0, 200}, {0, 50}|", _world.Get(Mod, "defaults"),
                "the Instance.new defaults of Full-API-Dump.json");
            StringAssert.Contains("expects a UDim2", _world.Get(Mod, "bad"));
            StringAssert.Contains("expects an Enum.TextXAlignment item", _world.Get(Mod, "badenum"));
        }

        private const string LocalPlayerHud = @"
local player = game:GetService('Players').LocalPlayer
store_set('local', tostring(player))
if player then
  local gui = Instance.new('ScreenGui')
  gui.Name = 'Mine'
  Instance.new('TextLabel').Parent = gui
  gui.Parent = player.PlayerGui
end";

        [Test]
        public void MVP15_Lua_AModLoadedAsTheLocalPlayersActor_ReachesItsPlayerGuiThroughLocalPlayer()
        {
            NewWorld();
            ActorContext localActor = new LocalActorIdentityProvider("local-player")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            _world.Stack.Runtime.LoadMod(localActor, Mod, LocalPlayerHud, persistToStore: false);
            _world.Frame();

            Assert.AreEqual(LocalPlayer().Name, _world.Get(Mod, "local"),
                "the bundled control script runs this way and relies on LocalPlayer");
            RbxInstance mine = LocalPlayer().FindFirstChildOfClass("PlayerGui").FindFirstChild("Mine");
            Assert.IsNotNull(mine);
            Assert.IsNotNull(Presenter.GetScreenPort((RbxScreenGui)mine), "the screen at this machine draws it");
        }

        [Test]
        public void MVP15_Lua_AHostLoadedMod_SeesNoLocalPlayer_AndStillReachesThePlayerThroughPlayers()
        {
            NewWorld();

            _world.Load(Mod, LocalPlayerHud + @"
local viaPlayers = game:GetService('Players'):GetPlayers()[1]
local gui = Instance.new('ScreenGui')
gui.Name = 'ViaPlayers'
gui.Parent = viaPlayers.PlayerGui");
            _world.Frame();

            Assert.AreEqual("nil", _world.Get(Mod, "local"), "a host-loaded mod is a server script");
            RbxInstance playerGui = LocalPlayer().FindFirstChildOfClass("PlayerGui");
            Assert.IsNull(playerGui.FindFirstChild("Mine"));
            Assert.IsNotNull(Presenter.GetScreenPort((RbxScreenGui)playerGui.FindFirstChild("ViaPlayers")),
                "the same PlayerGui, reached through Players, is drawn");
        }
    }
}
