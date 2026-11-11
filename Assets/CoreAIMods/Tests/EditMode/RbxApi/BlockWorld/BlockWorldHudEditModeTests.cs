using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using CoreAI.RuntimeUi;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the Block World's HUD mod (<c>blockworld_hud</c>): the markup the mod hands to <c>UxmlService</c> passes
    /// the real UXML and USS interpreter with zero reports, and the hotbar is chosen by the number keys, the wheel
    /// (wrapping both ways) and a click on a slot, while input the UI took is ignored; the status line follows the
    /// attributes the other mods publish. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldHudEditModeTests
    {
        private const string Mod = "blockworld_hud";
        private const string ScreenId = Mod + "/hud";

        private SynchronizationContext _savedContext;
        private BlockWorldTestWorld _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = new BlockWorldTestWorld();
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

        private FakeRbxUiBackend.FakeScreen Screen
        {
            get
            {
                FakeRbxUiBackend.FakeScreen screen = _world.Ui.Get(ScreenId);
                Assert.IsNotNull(screen, "the HUD mod owns one screen, 'hud'");
                return screen;
            }
        }

        private int SelectedSlot()
        {
            int selected = 0;
            for (int index = 1; index <= 9; index++)
            {
                if (Screen.Find("slot" + index).Classes.Contains("is-selected"))
                {
                    Assert.AreEqual(0, selected, "exactly one slot is selected");
                    selected = index;
                }
            }

            return selected;
        }

        private static string Describe(IReadOnlyList<RuntimeUiDiagnostic> diagnostics)
        {
            string text = "";
            foreach (RuntimeUiDiagnostic diagnostic in diagnostics)
            {
                text += diagnostic + "\n";
            }

            return text;
        }

        [Test]
        public void P6_Hud_TheMarkup_PassesTheRealInterpreter_WithZeroReports()
        {
            UxmlDocument document = UxmlParser.Parse(Screen.UxmlText);
            UssStyleSheet sheet = UssStyleSheet.Parse(Screen.UssText);

            Assert.AreEqual(0, document.Diagnostics.Count, Describe(document.Diagnostics));
            Assert.AreEqual(0, sheet.Diagnostics.Count, Describe(sheet.Diagnostics));
            Assert.Greater(document.ElementCount, 45, "a crosshair, a status line, notices and nine slots of four parts");
            RuntimeUiTheme theme = new();
            foreach (Match match in Regex.Matches(Screen.UssText, @"var\((--[a-z0-9-]+)"))
            {
                Assert.IsTrue(theme.TryGetToken(match.Groups[1].Value, out _), match.Groups[1].Value);
            }

            CollectionAssert.IsEmpty(Screen.Diagnostics, "the mod's own screen reports nothing either");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Hud_ARuleTheInterpreterRefuses_IsReported_SoTheCheckAboveCanFail()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(Screen.UssText.Replace("padding: 0;", "gap: 4px;"));

            Assert.AreEqual(1, sheet.Diagnostics.Count, Describe(sheet.Diagnostics));
        }

        [Test]
        public void P6_Hud_TheFirstSlot_IsInYourHandAtTheStart()
        {
            Assert.AreEqual(1, SelectedSlot());
            Assert.AreEqual("Grass", _world.Attribute("BlockWorldSelected"));
            Assert.AreEqual("Grass", Screen.Find("itemname").Text);
        }

        [Test]
        public void P6_Hud_NumberKeys_ChooseTheirSlot()
        {
            _world.Tap(BlockWorldTestWorld.KeyOne + 2);
            Assert.AreEqual(3, SelectedSlot());
            Assert.AreEqual("Rock", _world.Attribute("BlockWorldSelected"));
            Assert.AreEqual("Stone", Screen.Find("itemname").Text, "the hand shows the friendly name");

            _world.Tap(BlockWorldTestWorld.KeyOne + 8);
            Assert.AreEqual(9, SelectedSlot());
            Assert.AreEqual("Torch", _world.Attribute("BlockWorldSelected"));
            Assert.AreEqual(9d, _world.Number("BlockWorldSlot"));
        }

        [Test]
        public void P6_Hud_TheWheel_StepsThroughTheSlots_AndWrapsBothWays()
        {
            _world.Wheel(-1f);
            Assert.AreEqual(2, SelectedSlot(), "a notch towards you is the next slot");
            _world.Wheel(1f);
            _world.Wheel(1f);
            Assert.AreEqual(9, SelectedSlot(), "past the first slot comes the last");
            _world.Wheel(-1f);
            Assert.AreEqual(1, SelectedSlot(), "past the last slot comes the first");
        }

        [Test]
        public void P6_Hud_AClickOnASlot_ChoosesIt()
        {
            Screen.Find("slot6").ClickByPlayer();
            _world.Frame();

            Assert.AreEqual(6, SelectedSlot());
            Assert.AreEqual("Brick", _world.Attribute("BlockWorldSelected"));
        }

        [Test]
        public void P6_Hud_KeysAndTheWheel_TheUiTook_ChooseNothing_NegativeTwin()
        {
            _world.Focus.SetCaptured("chat", true);

            _world.Tap(BlockWorldTestWorld.KeyOne + 4);
            _world.Wheel(-1f);

            Assert.AreEqual(1, SelectedSlot(), "typing '5' into the chat does not change the hand");
            _world.Focus.SetCaptured("chat", false);
            _world.Tap(BlockWorldTestWorld.KeyOne + 4);
            Assert.AreEqual(5, SelectedSlot());
        }

        [Test]
        public void P6_Hud_TheStatusLine_FollowsTheClockTheModeAndTheCounters()
        {
            _world.LoadSource("test_clock", "game:GetService('Lighting').ClockTime = 21.5");
            _world.SetAttribute("BlockWorldPlaced", 7d);
            _world.SetAttribute("BlockWorldBroken", 3d);
            _world.SetAttribute("BlockWorldCreative", false);
            _world.SetAttribute("BlockWorldCount_Brick", 12d);
            _world.Frame(20);

            Assert.AreEqual("21:30  Night", Screen.Find("clock").Text);
            Assert.AreEqual("Survival", Screen.Find("mode").Text);
            Assert.AreEqual("Placed 7  Broken 3", Screen.Find("counter").Text);
            Assert.AreEqual("12", Screen.Find("count6").Text, "survival shows what you carry");
            Assert.AreEqual("0", Screen.Find("count1").Text);

            _world.SetAttribute("BlockWorldCreative", true);
            _world.Frame();
            Assert.AreEqual("", Screen.Find("count6").Text, "creative hides the counts: they are endless");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Hud_ANotice_ShowsForFourSeconds()
        {
            FakeRbxUiBackend.FakeElement notice = Screen.Find("notice");
            Assert.IsTrue(notice.Classes.Contains("is-hidden"), "nothing to say at the start");

            _world.SetAttribute("BlockWorldNotice", "Torch budget reached.");
            _world.SetAttribute("BlockWorldNoticeAt", 1d);
            _world.Frame();

            Assert.AreEqual("Torch budget reached.", notice.Text);
            Assert.IsFalse(notice.Classes.Contains("is-hidden"));
            _world.Frame(250);
            Assert.IsTrue(notice.Classes.Contains("is-hidden"), "gone after four seconds");
        }
    }
}
