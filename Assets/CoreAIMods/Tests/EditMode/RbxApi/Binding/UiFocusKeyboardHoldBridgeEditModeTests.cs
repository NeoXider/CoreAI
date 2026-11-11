using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.UiFocus;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// The world's input while a CoreAI screen holds the keyboard (a Hub open over a play world): the bridge captures
    /// the world's input, so every event reaches Lua with <c>gameProcessedEvent = true</c>, and gives it back when the
    /// hold ends.
    /// </summary>
    [TestFixture]
    public sealed class UiFocusKeyboardHoldBridgeEditModeTests
    {
        [Test]
        public void AKeyboardHold_CapturesTheWorldsInput_AndTheReleaseGivesItBack()
        {
            CoreAiUiFocus ui = new();
            InputFocusState world = new();
            CoreAiUiFocusBridge bridge = new(ui, world);

            ui.ReportKeyboardHold("hub", true);
            Assert.IsTrue(world.IsGameInputCaptured, "keys and clicks are the Hub's while it is open");
            Assert.IsTrue(world.IsHeldBy(CoreAiUiFocusBridge.HolderId));

            ui.ReportKeyboardHold("hub", false);
            Assert.IsFalse(world.IsGameInputCaptured, "the closed Hub gives the world its keys back");
            bridge.Dispose();
        }

        [Test]
        public void HoveringTheHub_WithoutAHold_KeepsTheKeysTheWorlds()
        {
            CoreAiUiFocus ui = new();
            InputFocusState world = new();
            CoreAiUiFocusBridge bridge = new(ui, world);

            ui.Report("hub", false, true);

            Assert.IsFalse(world.IsGameInputCaptured, "a side panel under the pointer does not stop WASD");
            bridge.Dispose();
        }
    }
}
