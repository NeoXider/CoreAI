using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.UiFocus;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// P1 stage A: the CoreAI UI focus service (chat panel, Hub) and the bridge that mirrors it into a
    /// world's input focus, so text typed into CoreAI UI reaches Lua as consumed input.
    /// </summary>
    [TestFixture]
    public sealed class P1UiFocusBridgeEditModeTests
    {
        [Test]
        public void UiFocus_IsTheUnionOfSurfaces_AndAClearedSurfaceReleasesItsShare()
        {
            CoreAiUiFocus focus = new();
            int changes = 0;
            focus.Changed += () => changes++;

            focus.Report("chat", textFocused: true, pointerOver: false);
            focus.Report("hub", textFocused: false, pointerOver: true);
            Assert.IsTrue(focus.HasTextFocus);
            Assert.IsTrue(focus.IsPointerOverUi);

            focus.Clear("chat");
            Assert.IsFalse(focus.HasTextFocus);
            Assert.IsTrue(focus.IsPointerOverUi, "the Hub still holds the pointer");

            focus.Report("hub", false, false);
            Assert.IsFalse(focus.IsPointerOverUi);
            Assert.AreEqual(4, changes);
        }

        [Test]
        public void Bridge_MirrorsTextFocusIntoTheWorld_AndReleasesOnDispose()
        {
            CoreAiUiFocus ui = new();
            InputFocusState world = new();
            world.SetCaptured("game-menu", true);
            CoreAiUiFocusBridge bridge = new(ui, world);

            ui.Report("chat", true, false);
            Assert.IsTrue(world.IsHeldBy(CoreAiUiFocusBridge.HolderId));

            bridge.Dispose();
            Assert.IsFalse(world.IsHeldBy(CoreAiUiFocusBridge.HolderId));
            Assert.IsTrue(world.IsHeldBy("game-menu"), "a host's own claim is left alone");
        }

        [Test]
        public void Bridge_WithoutAWorld_IsInert()
        {
            CoreAiUiFocusBridge bridge = new(new CoreAiUiFocus(), null);

            Assert.IsFalse(bridge.IsActive);
            Assert.DoesNotThrow(bridge.Dispose);
        }

        [Test]
        public void PointerOverUi_CapturesTheMouseButNotTheKeys()
        {
            InputFocusState world = new();
            CoreAiUiFocus ui = new();
            CoreAiUiFocusBridge bridge = new(ui, world);

            ui.Report("hub", textFocused: false, pointerOver: true);

            Assert.IsTrue(world.IsPointerOverUi);
            Assert.IsFalse(world.IsGameInputCaptured, "hovering a panel must not stop WASD");
            bridge.Dispose();
            Assert.IsFalse(world.IsPointerOverUi);
        }
    }
}
