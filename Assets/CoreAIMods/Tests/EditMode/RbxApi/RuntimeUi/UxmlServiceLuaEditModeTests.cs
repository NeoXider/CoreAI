using System.Threading;
using CoreAI.Ai;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.UiFocus;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.RuntimeUi
{
    /// <summary>
    /// Lua-tier conformance of P18's Lua surface, <c>game:GetService("UxmlService")</c>, through the real
    /// mod runtime and the real Lua bindings over an engine-free fake host: screens from UXML and USS
    /// text, live replaces, queries, element reads and writes, the three signals, the <c>Ui</c>
    /// capability, per-mod ownership, unload and hot-reload cleanup, the budgets and the focus contract.
    /// Every rule has its negative twin. The same surface over the real runtime UI host is covered by
    /// <c>UxmlServiceRuntimeUiHostEditModeTests</c>.
    /// </summary>
    [TestFixture]
    public sealed class UxmlServiceLuaEditModeTests
    {
        private const string Mod = "m";

        private const string Capture = @"
local function capture(key, fn)
  local ok, err = pcall(fn)
  store_set(key .. '_ok', tostring(ok))
  store_set(key .. '_err', tostring(err))
end
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

        [Test]
        public void P18_CreateScreen_BuildsAScreenTheModOwns_UnderANamespacedHostId()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, @"
local ui = game:GetService('UxmlService')
local hud = ui:CreateScreen('hud',
  '<ui:Label name=""title"" text=""Hello"" /><ui:Button name=""go"" class=""btn"" text=""Go"" />',
  '.btn { color: red; }', { SortOrder = 5 })
store_set('id', hud.Id)
store_set('title', hud:GetElement('title').Text)
store_set('buttons', tostring(#hud:QueryAll('.btn')))
store_set('kind', hud:Query('#go').Kind)
store_set('sort', tostring(hud.SortOrder))
store_set('visible', tostring(hud.Visible))
store_set('same', tostring(ui:GetScreen('hud') == hud))
store_set('missing', tostring(ui:GetScreen('other')))");

            Assert.AreEqual("hud", _world.Get(Mod, "id"));
            Assert.AreEqual("Hello", _world.Get(Mod, "title"));
            Assert.AreEqual("1", _world.Get(Mod, "buttons"));
            Assert.AreEqual("Button", _world.Get(Mod, "kind"));
            Assert.AreEqual("5", _world.Get(Mod, "sort"));
            Assert.AreEqual("true", _world.Get(Mod, "visible"));
            Assert.AreEqual("true", _world.Get(Mod, "same"), "GetScreen returns the screen CreateScreen made.");
            Assert.AreEqual("nil", _world.Get(Mod, "missing"));
            FakeRbxUiBackend.FakeScreen screen = _world.Fake.Get("m/hud");
            Assert.IsNotNull(screen, "The host id is the mod id plus the mod-local id.");
            Assert.AreEqual(Mod, screen.Owner);
            Assert.AreEqual(5, screen.SortOrder);
            Assert.AreEqual(".btn { color: red; }", screen.UssText);
        }

        [Test]
        public void P18_ElementWrites_TextValueVisibilityClassesAndStyles_ReachTheHost()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, @"
local hud = game:GetService('UxmlService'):CreateScreen('hud',
  '<ui:Label name=""l"" text=""a"" /><ui:Toggle name=""t"" /><ui:Slider name=""s"" /><ui:TextField name=""f"" /><ui:ListView name=""list"" />')
local l = hud:GetElement('l')
l.Text = 'b'
l.Visible = false
l.Enabled = false
l.ZOrder = 3
l:AddClass('one')
l:AddClass('two')
l:RemoveClass('one')
store_set('toggled', tostring(l:ToggleClass('three')))
store_set('has', tostring(l:HasClass('two')) .. ',' .. tostring(l:HasClass('one')))
store_set('classes', tostring(#l:GetClasses()))
store_set('style', tostring(l:SetStyle('color', 'red')))
store_set('read_style', tostring(l:GetStyle('color')))
store_set('rejected', tostring(l:SetStyle('gap', '4px')))
store_set('cleared', tostring(l:ClearStyle('color')))
store_set('attr', tostring(l:SetAttribute('tooltip', 'hi')))
hud:GetElement('t').Value = true
hud:GetElement('s').Value = 0.5
hud:GetElement('f').Value = 'typed'
hud:GetElement('list'):SetItems({ 'Stone', 'Dirt' })
store_set('t', tostring(hud:GetElement('t').Value))
store_set('s', tostring(hud:GetElement('s').Value))
store_set('f', hud:GetElement('f').Value)
store_set('text', l.Text)
store_set('enabled', tostring(l.Enabled))");

            FakeRbxUiBackend.FakeElement label = _world.Fake.Get("m/hud").Find("l");
            Assert.AreEqual("b", label.Text);
            Assert.IsFalse(label.Visible);
            Assert.IsFalse(label.Enabled);
            Assert.AreEqual(3, label.ZOrder);
            CollectionAssert.AreEquivalent(new[] { "two", "three" }, label.Classes);
            Assert.AreEqual("true", _world.Get(Mod, "toggled"));
            Assert.AreEqual("true,false", _world.Get(Mod, "has"));
            Assert.AreEqual("2", _world.Get(Mod, "classes"));
            Assert.AreEqual("true", _world.Get(Mod, "style"));
            Assert.AreEqual("red", _world.Get(Mod, "read_style"));
            Assert.AreEqual("false", _world.Get(Mod, "rejected"),
                "An unsupported property is returned as false (and reported), never raised.");
            Assert.AreEqual("true", _world.Get(Mod, "cleared"));
            Assert.IsFalse(label.Styles.ContainsKey("color"));
            Assert.AreEqual("hi", label.Attributes["tooltip"]);
            Assert.AreEqual("true", _world.Get(Mod, "t"));
            Assert.AreEqual("0.5", _world.Get(Mod, "s"));
            Assert.AreEqual("typed", _world.Get(Mod, "f"));
            CollectionAssert.AreEqual(new[] { "Stone", "Dirt" }, _world.Fake.Get("m/hud").Find("list").Items);
            Assert.AreEqual("b", _world.Get(Mod, "text"));
            Assert.AreEqual("false", _world.Get(Mod, "enabled"));
        }

        [Test]
        public void P18_AValueOnALabel_IsAnUnsupportedOperation_WhileAToggleTakesIt()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, Capture + @"
local hud = game:GetService('UxmlService'):CreateScreen('hud',
  '<ui:Label name=""l"" /><ui:Toggle name=""t"" /><ui:Button name=""b"" /><ui:ListView name=""list"" />')
capture('label', function() hud:GetElement('l').Value = true end)
capture('toggle', function() hud:GetElement('t').Value = true end)
capture('items_button', function() hud:GetElement('b'):SetItems({ 'x' }) end)
capture('items_list', function() hud:GetElement('list'):SetItems({ 'x' }) end)
capture('text_box', function() hud:CreateElement('VisualElement').Text = 'x' end)");

            Assert.AreEqual("false", _world.Get(Mod, "label_ok"));
            StringAssert.Contains(RbxUiErrors.UnsupportedOperation, _world.Get(Mod, "label_err"));
            Assert.AreEqual("true", _world.Get(Mod, "toggle_ok"), _world.Get(Mod, "toggle_err"));
            Assert.AreEqual("false", _world.Get(Mod, "items_button_ok"));
            StringAssert.Contains(RbxUiErrors.UnsupportedOperation, _world.Get(Mod, "items_button_err"));
            Assert.AreEqual("true", _world.Get(Mod, "items_list_ok"), _world.Get(Mod, "items_list_err"));
            Assert.AreEqual("false", _world.Get(Mod, "text_box_ok"));
            StringAssert.Contains(RbxUiErrors.UnsupportedOperation, _world.Get(Mod, "text_box_err"));
        }

        [Test]
        public void P18_SetUss_RestylesTheSameTree_AndSetUxml_KeepsNamedElementsAndDetachesTheRest()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, Capture + @"
local hud = game:GetService('UxmlService'):CreateScreen('hud',
  '<ui:TextField name=""f"" /><ui:Label class=""tip"" text=""unnamed"" />', '.a { color: red; }')
local field = hud:GetElement('f')
local tip = hud:Query('.tip')
field.Text = 'kept'
hud:SetUss('.a { color: blue; }')
store_set('after_uss', tostring(field.IsAlive) .. ',' .. tostring(tip.IsAlive))
hud:SetUxml('<ui:VisualElement><ui:TextField name=""f"" /></ui:VisualElement><ui:Label class=""tip"" />')
store_set('field_alive', tostring(field.IsAlive))
store_set('field_text', field.Text)
store_set('tip_alive', tostring(tip.IsAlive))
store_set('tip_text', tostring(tip.Text))
capture('tip_write', function() tip.Text = 'x' end)");

            FakeRbxUiBackend.FakeScreen screen = _world.Fake.Get("m/hud");
            Assert.AreEqual("true,true", _world.Get(Mod, "after_uss"), "A USS replace keeps the tree and every handle.");
            Assert.AreEqual(2, screen.BuildCount, "Create plus one UXML replace rebuilt the tree; the USS replace did not.");
            Assert.AreEqual("true", _world.Get(Mod, "field_alive"), "A named element survives a UXML replace.");
            Assert.AreEqual("kept", _world.Get(Mod, "field_text"), "Its state carries over.");
            Assert.AreEqual("false", _world.Get(Mod, "tip_alive"), "An unnamed element detaches.");
            Assert.AreEqual("nil", _world.Get(Mod, "tip_text"), "Reads on a detached element return nil.");
            Assert.AreEqual("false", _world.Get(Mod, "tip_write_ok"));
            StringAssert.Contains(RbxUiErrors.ElementDetached, _world.Get(Mod, "tip_write_err"),
                "Writes on a detached element raise.");
        }

        [Test]
        public void P18_InterpreterReports_AreReturnedAsDiagnostics_NeverRaised()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, @"
local ui = game:GetService('UxmlService')
local broken = ui:CreateScreen('broken', '<ui:Blink name=""x"" />', '.a { gap: 4px; }')
local codes = {}
for _, report in ipairs(broken.Diagnostics) do
  table.insert(codes, report.Code .. '@' .. report.Source .. ':' .. tostring(report.Line))
end
store_set('broken', table.concat(codes, ' '))
local fromSet = broken:SetUss('.a { gap: 2px; }')
store_set('set_uss', tostring(#fromSet) .. ' ' .. fromSet[1].Code .. ' ' .. fromSet[1].Severity)
local clean = ui:CreateScreen('clean', '<ui:Label name=""x"" />', '.a { color: red; }')
store_set('clean', tostring(#clean.Diagnostics))");

            StringAssert.Contains("UXML_UNKNOWN_ELEMENT@uxml:1", _world.Get(Mod, "broken"));
            StringAssert.Contains("USS_UNSUPPORTED_PROPERTY@uss:1", _world.Get(Mod, "broken"));
            Assert.AreEqual("1 USS_UNSUPPORTED_PROPERTY Warning", _world.Get(Mod, "set_uss"));
            Assert.AreEqual("0", _world.Get(Mod, "clean"), "A text the interpreter accepts yields no reports.");
        }

        [Test]
        public void P18_Clicked_RunsTheLuaHandlerDeferred_ThroughTheModScheduler()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, @"
local hud = game:GetService('UxmlService'):CreateScreen('hud', '<ui:Button name=""b"" />')
local clicks = 0
hud:GetElement('b').Clicked:Connect(function(...)
  clicks = clicks + 1
  store_set('clicks', tostring(clicks))
  store_set('args', tostring(select('#', ...)))
end)");

            FakeRbxUiBackend.FakeElement button = _world.Fake.Get("m/hud").Find("b");
            button.ClickByPlayer();
            Assert.AreEqual("", _world.Get(Mod, "clicks"), "Handlers never run inside the UI event that fired them.");
            _world.Frame();
            Assert.AreEqual("1", _world.Get(Mod, "clicks"));
            Assert.AreEqual("0", _world.Get(Mod, "args"), "Clicked passes no arguments.");
            button.ClickByPlayer();
            _world.Frame();
            Assert.AreEqual("2", _world.Get(Mod, "clicks"));
        }

        [Test]
        public void P18_ValueChangedAndSubmitted_PassPlainValues()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, @"
local hud = game:GetService('UxmlService'):CreateScreen('hud',
  '<ui:Toggle name=""t"" /><ui:Slider name=""s"" /><ui:TextField name=""f"" />')
hud:GetElement('t').ValueChanged:Connect(function(v) store_set('t', type(v) .. ':' .. tostring(v)) end)
hud:GetElement('s').ValueChanged:Connect(function(v) store_set('s', type(v) .. ':' .. tostring(v)) end)
hud:GetElement('f').Submitted:Connect(function(v) store_set('f', type(v) .. ':' .. v) end)");

            FakeRbxUiBackend.FakeScreen screen = _world.Fake.Get("m/hud");
            screen.Find("t").ChangeValueByPlayer(RbxUiValue.FromBoolean(true));
            screen.Find("s").ChangeValueByPlayer(RbxUiValue.FromNumber(0.25d));
            screen.Find("f").ChangeValueByPlayer(RbxUiValue.FromText("hello"));
            screen.Find("f").SubmitByPlayer();
            _world.Frame();

            Assert.AreEqual("boolean:true", _world.Get(Mod, "t"));
            Assert.AreEqual("number:0.25", _world.Get(Mod, "s"));
            Assert.AreEqual("string:hello", _world.Get(Mod, "f"));
        }

        [Test]
        public void P18_WithoutTheUiCapability_EveryMemberIsRefused()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, Capture + @"
local ui = game:GetService('UxmlService')
capture('create', function() ui:CreateScreen('hud', '<ui:Label />') end)
capture('focus', function() return ui.TextInputFocused end)", LuaCapabilities.All & ~LuaCapabilities.Ui);

            Assert.AreEqual("false", _world.Get(Mod, "create_ok"));
            StringAssert.Contains(RbxUiErrors.CapabilityRequired, _world.Get(Mod, "create_err"));
            Assert.AreEqual("false", _world.Get(Mod, "focus_ok"));
            StringAssert.Contains(RbxUiErrors.CapabilityRequired, _world.Get(Mod, "focus_err"));
            Assert.AreEqual(0, _world.Fake.Screens.Count, "Nothing was created.");
        }

        [Test]
        public void P18_WithTheUiCapability_TheScreenRenders()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, Capture + @"
capture('create', function() game:GetService('UxmlService'):CreateScreen('hud', '<ui:Label name=""l"" />') end)",
                LuaCapabilities.Read | LuaCapabilities.Ui);

            Assert.AreEqual("true", _world.Get(Mod, "create_ok"), _world.Get(Mod, "create_err"));
            Assert.IsNotNull(_world.Fake.Get("m/hud"));
        }

        [Test]
        public void P18_AllGrantsUi_SoADefaultGameplayModDrawsWithoutConfiguration()
        {
            Assert.AreEqual(LuaCapabilities.Ui, LuaCapabilities.All & LuaCapabilities.Ui);
            Assert.AreEqual(0, (int)(LuaCapabilities.Read & LuaCapabilities.Ui), "Ui is its own tier.");
        }

        [Test]
        public void P18_TwoModsMayBothOwnAHud_AndEachSeesOnlyItsOwn()
        {
            _world = UxmlServiceTestWorld.WithFake();
            const string source = @"
local ui = game:GetService('UxmlService')
ui:CreateScreen('hud', '<ui:Label name=""who"" text=""' .. OWNER .. '"" />')
local screens = ui:GetScreens()
store_set('count', tostring(#screens))
store_set('who', ui:GetScreen('hud'):GetElement('who').Text)";

            _world.Load("a", "local OWNER = 'a'" + source);
            _world.Load("b", "local OWNER = 'b'" + source);

            Assert.IsNotNull(_world.Fake.Get("a/hud"));
            Assert.IsNotNull(_world.Fake.Get("b/hud"));
            Assert.AreEqual("1", _world.Get("b", "count"), "A mod's GetScreens lists only its own screens.");
            Assert.AreEqual("b", _world.Get("b", "who"), "GetScreen resolves the caller's own id.");
            Assert.AreEqual("a", _world.Get("a", "who"));
        }

        [Test]
        public void P18_Unload_RemovesTheModsScreensAndHandlers()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, @"
local hud = game:GetService('UxmlService'):CreateScreen('hud', '<ui:Button name=""b"" />')
hud:GetElement('b').Clicked:Connect(function() store_set('clicked', 'yes') end)");
            FakeRbxUiBackend.FakeScreen screen = _world.Fake.Get("m/hud");
            FakeRbxUiBackend.FakeElement button = screen.Find("b");

            Assert.IsTrue(_world.Unload(Mod));
            button.ClickByPlayer();
            _world.Frame();

            Assert.IsTrue(screen.IsDestroyed, "Unloading the mod destroyed its screen.");
            Assert.AreEqual(0, _world.Fake.Screens.Count);
            Assert.AreEqual(0, _world.Fake.SubscriberCount, "No element event subscription remains.");
            Assert.AreEqual(0, _world.Connections.GetOwnedBy(Mod).Count, "No connection of the mod remains.");
            Assert.AreEqual(0, _world.World.CountScreens(Mod));
            Assert.AreEqual("", _world.Get(Mod, "clicked"), "The torn-down handler never ran.");
        }

        [Test]
        public void P18_ALoadedMod_KeepsItsScreensAndHandlersFrameAfterFrame()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, @"
local hud = game:GetService('UxmlService'):CreateScreen('hud', '<ui:Button name=""b"" />')
hud:GetElement('b').Clicked:Connect(function() store_set('clicked', 'yes') end)");
            FakeRbxUiBackend.FakeScreen screen = _world.Fake.Get("m/hud");

            _world.Frame();
            _world.Frame();
            screen.Find("b").ClickByPlayer();
            _world.Frame();

            Assert.IsFalse(screen.IsDestroyed);
            Assert.AreEqual(1, _world.Fake.SubscriberCount);
            Assert.AreEqual("yes", _world.Get(Mod, "clicked"));
        }

        [Test]
        public void P18_HotReload_ReplacesTheReCreatedScreenKeepingState_AndDropsScreensNotReCreated()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, @"
local ui = game:GetService('UxmlService')
local hud = ui:CreateScreen('hud', '<ui:Toggle name=""t"" /><ui:Button name=""b"" />')
ui:CreateScreen('debug', '<ui:Label />')
hud:GetElement('b').Clicked:Connect(function() store_set('v1', 'ran') end)");
            FakeRbxUiBackend.FakeScreen hud = _world.Fake.Get("m/hud");
            FakeRbxUiBackend.FakeScreen debug = _world.Fake.Get("m/debug");
            hud.Find("t").ChangeValueByPlayer(RbxUiValue.FromBoolean(true));

            _world.Reload(Mod, @"
local ui = game:GetService('UxmlService')
local hud = ui:CreateScreen('hud', '<ui:Toggle name=""t"" /><ui:Button name=""b"" /><ui:Label name=""new"" />')
store_set('kept', tostring(hud:GetElement('t').Value))
hud:GetElement('b').Clicked:Connect(function() store_set('v2', 'ran') end)");
            _world.Frame();

            Assert.AreSame(hud, _world.Fake.Get("m/hud"), "The re-created screen was replaced in place, not rebuilt from scratch.");
            Assert.IsFalse(hud.IsDestroyed);
            Assert.AreEqual(2, hud.BuildCount);
            Assert.AreEqual("true", _world.Get(Mod, "kept"), "Element state survives the reload.");
            Assert.IsTrue(debug.IsDestroyed, "A screen the reloaded script did not re-create is destroyed.");
            hud.Find("b").ClickByPlayer();
            _world.Frame();
            Assert.AreEqual("ran", _world.Get(Mod, "v2"));
            Assert.AreEqual("", _world.Get(Mod, "v1"), "The outgoing run's handler was disconnected.");
        }

        [Test]
        public void P18_HotReload_KeepsAScreenTheNewRunOnlyLooksUp()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, @"game:GetService('UxmlService'):CreateScreen('hud', '<ui:Label name=""l"" />')");
            FakeRbxUiBackend.FakeScreen hud = _world.Fake.Get("m/hud");

            _world.Reload(Mod, @"store_set('found', tostring(game:GetService('UxmlService'):GetScreen('hud') ~= nil))");
            _world.Frame();

            Assert.AreEqual("true", _world.Get(Mod, "found"));
            Assert.IsFalse(hud.IsDestroyed, "GetScreen from the new run claims the screen.");
        }

        [Test]
        public void P18_ScreenBudget_RefusesTheScreenPastTheLimit()
        {
            _world = UxmlServiceTestWorld.WithFake(new RbxUiBudgets { MaxScreensPerMod = 2 });
            _world.Load(Mod, Capture + @"
local ui = game:GetService('UxmlService')
capture('first', function() ui:CreateScreen('a', '') end)
capture('second', function() ui:CreateScreen('b', '') end)
capture('again', function() ui:CreateScreen('a', '<ui:Label />') end)
capture('third', function() ui:CreateScreen('c', '') end)");

            Assert.AreEqual("true", _world.Get(Mod, "second_ok"), _world.Get(Mod, "second_err"));
            Assert.AreEqual("true", _world.Get(Mod, "again_ok"), "Re-creating an owned id replaces it and costs nothing.");
            Assert.AreEqual("false", _world.Get(Mod, "third_ok"));
            StringAssert.Contains("BUDGET_EXCEEDED: " + RbxUiErrors.BudgetScreens, _world.Get(Mod, "third_err"));
        }

        [Test]
        public void P18_ElementBudget_CountsEveryScreenOfTheMod()
        {
            _world = UxmlServiceTestWorld.WithFake(new RbxUiBudgets { MaxElementsPerMod = 3 });
            _world.Load(Mod, Capture + @"
local ui = game:GetService('UxmlService')
capture('fits', function() ui:CreateScreen('a', '<ui:Label /><ui:Label />') end)
capture('over', function() ui:CreateScreen('b', '<ui:Label /><ui:Label />') end)
capture('last', function() ui:GetScreen('a'):CreateElement('Label') end)
capture('past', function() ui:GetScreen('a'):CreateElement('Label') end)");

            Assert.AreEqual("true", _world.Get(Mod, "fits_ok"), _world.Get(Mod, "fits_err"));
            Assert.AreEqual("false", _world.Get(Mod, "over_ok"));
            StringAssert.Contains(RbxUiErrors.BudgetElements, _world.Get(Mod, "over_err"));
            Assert.AreEqual("true", _world.Get(Mod, "last_ok"), _world.Get(Mod, "last_err"));
            Assert.AreEqual("false", _world.Get(Mod, "past_ok"));
            StringAssert.Contains(RbxUiErrors.BudgetElements, _world.Get(Mod, "past_err"));
        }

        [Test]
        public void P18_TextBudget_RefusesAnOversizedText()
        {
            _world = UxmlServiceTestWorld.WithFake(new RbxUiBudgets { MaxTextLength = 40 });
            _world.Load(Mod, Capture + @"
local ui = game:GetService('UxmlService')
capture('short', function() ui:CreateScreen('a', '<ui:Label />') end)
capture('long', function() ui:CreateScreen('b', string.rep('<ui:Label />', 10)) end)
capture('uss', function() ui:GetScreen('a'):SetUss(string.rep('.a{}', 20)) end)");

            Assert.AreEqual("true", _world.Get(Mod, "short_ok"), _world.Get(Mod, "short_err"));
            Assert.AreEqual("false", _world.Get(Mod, "long_ok"));
            StringAssert.Contains("PAYLOAD_TOO_LARGE: " + RbxUiErrors.TextTooLarge, _world.Get(Mod, "long_err"));
            Assert.AreEqual("false", _world.Get(Mod, "uss_ok"));
            StringAssert.Contains(RbxUiErrors.TextTooLarge, _world.Get(Mod, "uss_err"));
        }

        [Test]
        public void P18_WriteBudget_RefusesWritesPastTheFrameBudget_AndResetsNextFrame()
        {
            _world = UxmlServiceTestWorld.WithFake(new RbxUiBudgets { MaxWritesPerFrame = 3 });
            _world.Load(Mod, Capture + @"
local l = game:GetService('UxmlService'):CreateScreen('hud', '<ui:Label name=""l"" />'):GetElement('l')
capture('writes', function()
  l.Text = '1'
  l.Text = '2'
  l.Text = '3'
end)
capture('fourth', function() l.Text = '4' end)
game:GetService('RunService').Heartbeat:Connect(function()
  capture('next_frame', function() l.Text = '5' end)
end)");

            Assert.AreEqual("true", _world.Get(Mod, "writes_ok"), _world.Get(Mod, "writes_err"));
            Assert.AreEqual("false", _world.Get(Mod, "fourth_ok"));
            StringAssert.Contains(RbxUiErrors.BudgetWrites, _world.Get(Mod, "fourth_err"));
            _world.Frame();
            _world.Frame();
            Assert.AreEqual("true", _world.Get(Mod, "next_frame_ok"), _world.Get(Mod, "next_frame_err"));
            Assert.AreEqual("5", _world.Fake.Get("m/hud").Find("l").Text);
        }

        [Test]
        public void P18_WithoutARuntimeUiHost_TheServiceSaysRuntimeUiIsNotAvailable()
        {
            _world = new UxmlServiceTestWorld(null);
            _world.Load(Mod, Capture + @"
local ui = game:GetService('UxmlService')
capture('create', function() ui:CreateScreen('hud', '<ui:Label />') end)
capture('pointer', function() return ui.PointerOverUi end)");

            Assert.AreEqual("false", _world.Get(Mod, "create_ok"));
            StringAssert.Contains(RbxUiErrors.Unavailable, _world.Get(Mod, "create_err"));
            StringAssert.Contains("runtime UI is not available", _world.Get(Mod, "create_err"));
            Assert.AreEqual("false", _world.Get(Mod, "pointer_ok"));
            StringAssert.Contains(RbxUiErrors.Unavailable, _world.Get(Mod, "pointer_err"));
        }

        [Test]
        public void P18_WhenTheHostSwitchedRuntimeUiOff_TheServiceSaysDisabled()
        {
            FakeRbxUiBackend backend = new() { IsEnabled = false };
            _world = new UxmlServiceTestWorld(backend);
            _world.Load(Mod, Capture + @"
capture('create', function() game:GetService('UxmlService'):CreateScreen('hud', '<ui:Label />') end)");

            Assert.AreEqual("false", _world.Get(Mod, "create_ok"));
            StringAssert.Contains(RbxUiErrors.Disabled, _world.Get(Mod, "create_err"));
        }

        [Test]
        public void P18_AWorldTheCompositionNeverAttached_StillResolvesTheService_AndSaysUnavailable()
        {
            _world = new UxmlServiceTestWorld(new FakeRbxUiBackend(), attach: false);
            _world.Load(Mod, Capture + @"
capture('create', function() game:GetService('UxmlService'):CreateScreen('hud', '<ui:Label />') end)");

            Assert.AreEqual("false", _world.Get(Mod, "create_ok"));
            StringAssert.Contains(RbxUiErrors.Unavailable, _world.Get(Mod, "create_err"),
                "Needs the ServiceCatalog line that registers UxmlService on every DataModel.");
        }

        [Test]
        public void P18_Tokens_OverrideOnlyTheCallersScreens_AndFallBackToTheHostTheme()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load("a", Capture + @"
local ui = game:GetService('UxmlService')
ui:SetToken('--cai-color-accent', 'red')
ui:CreateScreen('hud', '<ui:Label />')
store_set('token', ui:GetToken('--cai-color-accent'))
capture('bad', function() ui:SetToken('accent', 'red') end)");
            _world.Load("b", @"
local ui = game:GetService('UxmlService')
ui:CreateScreen('hud', '<ui:Label />')
store_set('token', ui:GetToken('--cai-color-accent'))");

            Assert.AreEqual("red", _world.Fake.Get("a/hud").RootElement.Styles["--cai-color-accent"]);
            Assert.IsFalse(_world.Fake.Get("b/hud").RootElement.Styles.ContainsKey("--cai-color-accent"),
                "Another mod's screens keep the host theme.");
            Assert.AreEqual("red", _world.Get("a", "token"));
            Assert.AreEqual("rgb(80, 200, 255)", _world.Get("b", "token"));
            Assert.AreEqual("false", _world.Get("a", "bad_ok"));
            StringAssert.Contains(RbxUiErrors.InvalidArgument, _world.Get("a", "bad_err"));
        }

        [Test]
        public void P18_AnUnsupportedSelector_IsRaised_WhileASupportedOneQueries()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, Capture + @"
local hud = game:GetService('UxmlService'):CreateScreen('hud',
  '<ui:VisualElement name=""bar""><ui:Button class=""slot"" /><ui:Button class=""slot"" /></ui:VisualElement>')
capture('bad', function() hud:QueryAll('Button + Button') end)
capture('good', function() store_set('found', tostring(#hud:QueryAll('#bar > .slot'))) end)");

            Assert.AreEqual("false", _world.Get(Mod, "bad_ok"));
            StringAssert.Contains(RbxUiErrors.InvalidSelector, _world.Get(Mod, "bad_err"));
            Assert.AreEqual("true", _world.Get(Mod, "good_ok"), _world.Get(Mod, "good_err"));
            Assert.AreEqual("2", _world.Get(Mod, "found"));
        }

        [Test]
        public void P18_Errors_UseTheStructuredShape_WithACodeAndAFix()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, Capture + @"
capture('bad_id', function() game:GetService('UxmlService'):CreateScreen('a/b', '') end)");

            string line = _world.Get(Mod, "bad_id_err");
            Assert.IsTrue(RbxError.TryParse(line, out RbxError error), line);
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            Assert.AreEqual(RbxUiErrors.InvalidArgument, RbxUiErrors.GetUiCode(error));
            Assert.AreEqual(Mod, error.ModId, "The error names the mod.");
            Assert.IsFalse(string.IsNullOrEmpty(error.Fix), "Every UI error carries a suggested fix.");
        }

        [Test]
        public void P18_CreateElement_BuildsWithoutMarkup_AndRefusesAnUnknownKind()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, Capture + @"
local hud = game:GetService('UxmlService'):CreateScreen('hud', '')
capture('made', function()
  local frame = hud:CreateElement('VisualElement', nil, 'frame')
  local label = frame:CreateChild('Label', 'title')
  label.Text = 'Hi'
  store_set('parent', label.Parent.Name)
end)
capture('unknown', function() hud:CreateElement('Marquee') end)");

            Assert.AreEqual("true", _world.Get(Mod, "made_ok"), _world.Get(Mod, "made_err"));
            Assert.AreEqual("frame", _world.Get(Mod, "parent"));
            Assert.AreEqual("Hi", _world.Fake.Get("m/hud").Find("title").Text);
            Assert.AreEqual("false", _world.Get(Mod, "unknown_ok"));
            StringAssert.Contains(RbxUiErrors.InvalidArgument, _world.Get(Mod, "unknown_err"));
        }

        [Test]
        public void P18_DestroyedScreen_FiresDestroying_AndRefusesLaterWrites()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, Capture + @"
local ui = game:GetService('UxmlService')
local hud = ui:CreateScreen('hud', '<ui:Label name=""l"" />')
local l = hud:GetElement('l')
hud.Destroying:Connect(function() store_set('destroying', 'fired') end)
hud:Destroy()
store_set('alive', tostring(hud.IsAlive) .. ',' .. tostring(l.IsAlive))
store_set('lookup', tostring(ui:GetScreen('hud')))
capture('write', function() hud.Visible = false end)
capture('element', function() l.Text = 'x' end)");
            _world.Frame();

            Assert.AreEqual("fired", _world.Get(Mod, "destroying"));
            Assert.AreEqual("false,false", _world.Get(Mod, "alive"));
            Assert.AreEqual("nil", _world.Get(Mod, "lookup"));
            StringAssert.Contains(RbxUiErrors.ScreenDestroyed, _world.Get(Mod, "write_err"));
            StringAssert.Contains(RbxUiErrors.ScreenDestroyed, _world.Get(Mod, "element_err"));
            Assert.AreEqual(0, _world.Fake.Screens.Count);
        }

        [Test]
        public void P18_FocusAndPointer_AreReadable_AndTextInputFocusChangedIsAnOrdinarySignal()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(Mod, @"
local ui = game:GetService('UxmlService')
store_set('before', tostring(ui.TextInputFocused) .. ',' .. tostring(ui.PointerOverUi))
ui.TextInputFocusChanged:Connect(function(focused)
  store_set('changed', type(focused) .. ':' .. tostring(focused))
  store_set('now', tostring(ui.TextInputFocused))
end)");

            _world.Fake.SetTextFocus(true);
            _world.Frame();

            Assert.AreEqual("false,false", _world.Get(Mod, "before"));
            Assert.AreEqual("boolean:true", _world.Get(Mod, "changed"));
            Assert.AreEqual("true", _world.Get(Mod, "now"));
        }

        [Test]
        public void P18_TheHostFocusReachesTheSharedUiFocus_SoTheGameSeesInputAsProcessed()
        {
            CoreAiUiFocus uiFocus = new();
            _world = UxmlServiceTestWorld.WithFake(uiFocus: uiFocus);
            using CoreAiUiFocusBridge bridge = new(uiFocus, (InputFocusState)_world.Input.FocusState);

            _world.Fake.SetTextFocus(true);
            Assert.IsTrue(uiFocus.HasTextFocus);
            Assert.IsTrue(_world.Input.FocusState.IsGameInputCaptured, "A focused field captures the game's input.");
            _world.Fake.SetTextFocus(false);
            _world.Fake.SetPointerOver(true);
            Assert.IsTrue(uiFocus.IsPointerOverUi);
            _world.Fake.SetPointerOver(false);
            Assert.IsFalse(uiFocus.HasTextFocus || uiFocus.IsPointerOverUi, "Released state is released.");
            Assert.IsFalse(_world.Input.FocusState.IsGameInputCaptured);

            _world.Fake.SetTextFocus(true);
            _world.Surface.Dispose();
            Assert.IsFalse(uiFocus.HasTextFocus, "Disposing the surface drops its claim.");
        }

        [Test]
        public void P18_Hotbar_IsBuiltByItsLuaMod_AndSelectsSlotsByClickAndByKey()
        {
            _world = UxmlServiceTestWorld.WithFake();
            _world.Load(RuntimeUiHotbarFixture.ModId, RuntimeUiHotbarFixture.Source);
            FakeRbxUiBackend.FakeScreen hud = _world.Fake.Get(RuntimeUiHotbarFixture.HostScreenId);
            Assert.IsNotNull(hud);
            Assert.AreEqual(10, hud.SortOrder);
            Assert.AreEqual(8, hud.ElementCount, "Crosshair, hotbar, five slots and the counter.");
            AssertSelected(hud, 0);

            hud.Find("slot3").ClickByPlayer();
            _world.Frame();
            AssertSelected(hud, 2);

            _world.Input.PressKey(RuntimeUiHotbarFixture.KeyCodeOne);
            _world.Roblox.PumpInput();
            _world.Frame();
            AssertSelected(hud, 0);
        }

        [Test]
        public void P18_Hotbar_CountsWorldClicks_ButIgnoresKeysWhileATextFieldHasFocus()
        {
            CoreAiUiFocus uiFocus = new();
            _world = UxmlServiceTestWorld.WithFake(uiFocus: uiFocus);
            using CoreAiUiFocusBridge bridge = new(uiFocus, (InputFocusState)_world.Input.FocusState);
            _world.Load(RuntimeUiHotbarFixture.ModId, RuntimeUiHotbarFixture.Source);
            FakeRbxUiBackend.FakeScreen hud = _world.Fake.Get(RuntimeUiHotbarFixture.HostScreenId);

            _world.Input.SetMouseButton(0, true);
            _world.Roblox.PumpInput();
            _world.Frame();
            Assert.AreEqual("Blocks: 1", hud.Find("counter").Text, "A click into the world places a block.");

            _world.Fake.SetTextFocus(true);
            _world.Input.PressKey(RuntimeUiHotbarFixture.KeyCodeThree);
            _world.Roblox.PumpInput();
            _world.Frame();
            AssertSelected(hud, 0);

            _world.Input.ReleaseKey(RuntimeUiHotbarFixture.KeyCodeThree);
            _world.Roblox.PumpInput();
            _world.Fake.SetTextFocus(false);
            _world.Input.PressKey(RuntimeUiHotbarFixture.KeyCodeThree);
            _world.Roblox.PumpInput();
            _world.Frame();
            AssertSelected(hud, 2);
        }

        private static void AssertSelected(FakeRbxUiBackend.FakeScreen hud, int index)
        {
            for (int i = 0; i < RuntimeUiHotbarFixture.SlotNames.Length; i++)
            {
                Assert.AreEqual(i == index, hud.Find(RuntimeUiHotbarFixture.SlotNames[i]).HasClass("is-selected"),
                    RuntimeUiHotbarFixture.SlotNames[i] + (i == index ? " should" : " should not") + " be selected");
            }
        }
    }
}
