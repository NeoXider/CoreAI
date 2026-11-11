using System.Threading;
using CoreAI.Ai.LuaCs;
using CoreAI.RuntimeUi;
using CoreAI.UiFocus;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode.RbxApi.RuntimeUi
{
    /// <summary>
    /// The UxmlService Lua surface over the real runtime UI host (part 1) in a real UI Toolkit panel:
    /// the hotbar fixture builds one Lua-made screen the interpreter accepts without a single report, a
    /// real button submit reaches its Lua handler, a USS swap restyles without rebuilding and keeps the
    /// selected slot, unloading the mod leaves zero screens, elements, callbacks and subscribers, and a
    /// focused field reaches the shared UI focus. Unity only: it needs UI Toolkit, so it is not linked
    /// into the portable Lua suite.
    /// </summary>
    [TestFixture]
    public sealed class UxmlServiceRuntimeUiHostEditModeTests
    {
        private SynchronizationContext _savedContext;
        private GameObject _panelObject;
        private PanelSettings _panelSettings;
        private RuntimeUiHost _host;
        private CoreAiUiFocus _uiFocus;
        private UxmlServiceTestWorld _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _panelObject = new GameObject("UxmlServiceRuntimeUiHostEditModeTests");
            _panelObject.SetActive(false);
            UIDocument document = _panelObject.AddComponent<UIDocument>();
            document.panelSettings = _panelSettings;
            _panelObject.SetActive(true);
            _host = new RuntimeUiHost(new RuntimeUiHostOptions { AutoCreateSurface = false, PersistAcrossScenes = false });
            _host.AttachTo(document.rootVisualElement);
            _uiFocus = new CoreAiUiFocus();
            _world = new UxmlServiceTestWorld(new RuntimeUiHostBackend(_host), uiFocus: _uiFocus);
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _host?.Dispose();
            if (_panelObject != null)
            {
                Object.DestroyImmediate(_panelObject);
            }

            if (_panelSettings != null)
            {
                Object.DestroyImmediate(_panelSettings);
            }

            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public void P18_RealHost_TheHotbarIsOneLuaMadeScreen_AndASubmitReachesItsLuaHandler()
        {
            _world.Load(RuntimeUiHotbarFixture.ModId, RuntimeUiHotbarFixture.Source);

            Assert.IsTrue(_host.TryGetScreen(RuntimeUiHotbarFixture.HostScreenId, out RuntimeUiScreen screen));
            Assert.AreEqual(RuntimeUiHotbarFixture.ModId, screen.Owner);
            Assert.AreEqual(8, screen.ElementCount);
            Assert.AreEqual(0, screen.Diagnostics.Count,
                "The skill's hotbar example uses only what the interpreter supports: " + Describe(screen));
            AssertSelected(screen, 0);

            Press(screen.GetElement("slot3"));
            _world.Frame();

            AssertSelected(screen, 2);
        }

        [Test]
        public void P18_RealHost_ASubmitBeforeTheFrame_DoesNotRunTheHandlerInsideTheUiEvent()
        {
            _world.Load(RuntimeUiHotbarFixture.ModId, RuntimeUiHotbarFixture.Source);
            Assert.IsTrue(_host.TryGetScreen(RuntimeUiHotbarFixture.HostScreenId, out RuntimeUiScreen screen));

            Press(screen.GetElement("slot2"));

            AssertSelected(screen, 0);
        }

        [Test]
        public void P18_RealHost_SetUss_RestylesWithoutRebuilding_AndKeepsTheSelectedSlot()
        {
            _world.Load("m", @"
local hud = game:GetService('UxmlService'):CreateScreen('hud',
  '<ui:Button name=""slot"" class=""slot"" /><ui:Button name=""restyle"" />', '.slot { width: 40px; }')
hud:GetElement('slot'):AddClass('is-selected')
hud:GetElement('restyle').Clicked:Connect(function()
  hud:SetUss('.slot { width: 80px; } .slot.is-selected { border-width: 4px; }')
end)");
            Assert.IsTrue(_host.TryGetScreen("m/hud", out RuntimeUiScreen screen));
            int builds = screen.BuildVersion;
            int styles = screen.StyleVersion;

            Press(screen.GetElement("restyle"));
            _world.Frame();

            Assert.AreEqual(builds, screen.BuildVersion, "A USS swap does not rebuild the tree.");
            Assert.Greater(screen.StyleVersion, styles, "It restyles it.");
            Assert.AreEqual(".slot { width: 80px; } .slot.is-selected { border-width: 4px; }", screen.UssText);
            Assert.IsTrue(screen.GetElement("slot").HasClass("is-selected"), "The selected slot stays selected.");
        }

        [Test]
        public void P18_RealHost_Unload_LeavesNoScreenElementCallbackOrSubscriber()
        {
            _world.Load(RuntimeUiHotbarFixture.ModId, RuntimeUiHotbarFixture.Source);
            Assert.IsTrue(_host.TryGetScreen(RuntimeUiHotbarFixture.HostScreenId, out RuntimeUiScreen screen));
            Assert.Greater(screen.LiveSubscriberCount, 0, "The loaded hotbar listens to its slots.");

            Assert.IsTrue(_world.Unload(RuntimeUiHotbarFixture.ModId));
            _world.Frame();

            Assert.IsTrue(screen.IsDestroyed);
            Assert.AreEqual(0, _host.Screens.Count);
            Assert.AreEqual(0, screen.ElementCount);
            Assert.AreEqual(0, screen.LiveCallbackCount);
            Assert.AreEqual(0, screen.LiveSubscriberCount);
            Assert.AreEqual(0, _world.Connections.GetOwnedBy(RuntimeUiHotbarFixture.ModId).Count);
        }

        [Test]
        public void P18_RealHost_AFocusedTextField_ReachesTheSharedUiFocus()
        {
            _world.Load("m", @"game:GetService('UxmlService'):CreateScreen('chat', '<ui:TextField name=""input"" />')");
            Assert.IsTrue(_host.TryGetScreen("m/chat", out RuntimeUiScreen screen));
            Assert.IsFalse(_uiFocus.HasTextFocus);

            screen.GetElement("input").Focus();

            Assert.IsTrue(_host.IsTextInputFocused);
            Assert.IsTrue(_uiFocus.HasTextFocus, "The game sees typing as processed input.");
            screen.Hide();
            Assert.IsFalse(_uiFocus.HasTextFocus, "Hiding the screen releases it.");
        }

        private static void Press(RuntimeUiElement element)
        {
            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = element.VisualElement;
                element.VisualElement.SendEvent(submit);
            }
        }

        private static void AssertSelected(RuntimeUiScreen screen, int index)
        {
            for (int i = 0; i < RuntimeUiHotbarFixture.SlotNames.Length; i++)
            {
                Assert.AreEqual(i == index, screen.GetElement(RuntimeUiHotbarFixture.SlotNames[i]).HasClass("is-selected"),
                    RuntimeUiHotbarFixture.SlotNames[i] + (i == index ? " should" : " should not") + " be selected");
            }
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
