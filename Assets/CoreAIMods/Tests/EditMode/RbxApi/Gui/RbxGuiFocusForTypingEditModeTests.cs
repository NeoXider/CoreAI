using CoreAI.Mods.Rbx.Instances;
using CoreAI.RuntimeUi;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Gui
{
    /// <summary>
    /// <c>TextBox:CaptureFocus()</c> the way a chat opens (DEV-141): a box shown in the same handler takes focus once
    /// the frame's GUI update drew it, the keys held when the script asked reach the host so the opening keystroke
    /// does not type into the box, and a release before the update drops the waiting capture. Plus the engine-free
    /// filter the host applies to those keys, whatever the keyboard layout made of them.
    /// </summary>
    [TestFixture]
    public sealed class RbxGuiFocusForTypingEditModeTests
    {
        private const int KeyT = 116;
        private const int KeyReturn = 13;

        [Test]
        public void CaptureFocus_OnAShownBox_FocusesAtOnce_WithTheHeldKeys()
        {
            using RbxGuiTestWorld world = new();
            world.Presenter.HeldKeyboardKeys = () => new[] { KeyT };
            RbxTextBox box = world.Create<RbxTextBox>("TextBox", world.Screen());
            world.Frame();

            box.CaptureFocus();

            FakeRbxUiBackend.FakeElement field = world.Element(box);
            Assert.IsTrue(box.IsFocused);
            Assert.AreEqual(1, field.FocusForTypingCount);
            CollectionAssert.AreEqual(new[] { KeyT }, field.LastFocusHeldKeys);
        }

        [Test]
        public void CaptureFocus_OnABoxShownInTheSameFrame_WaitsForTheGuiUpdate()
        {
            using RbxGuiTestWorld world = new();
            world.Presenter.HeldKeyboardKeys = () => new[] { KeyReturn };
            RbxTextBox box = world.Create<RbxTextBox>("TextBox", world.Screen());
            box.SetVisible(false);
            world.Frame();
            FakeRbxUiBackend.FakeElement field = world.Element(box);
            Assert.IsFalse(field.Visible);

            box.SetVisible(true);
            box.CaptureFocus();
            Assert.IsFalse(box.IsFocused, "the field is still hidden: focus waits for the update");
            Assert.AreEqual(0, field.FocusForTypingCount);

            world.Presenter.Flush();

            Assert.IsTrue(field.Visible);
            Assert.IsTrue(box.IsFocused);
            CollectionAssert.AreEqual(new[] { KeyReturn }, field.LastFocusHeldKeys,
                "the keys held when the script asked, not when the update ran");
        }

        [Test]
        public void CaptureFocus_OnABoxCreatedInTheSameFrame_TakesFocusAfterTheUpdate()
        {
            using RbxGuiTestWorld world = new();
            RbxScreenGui screen = world.Screen();
            world.Frame();

            RbxTextBox box = world.Create<RbxTextBox>("TextBox", screen);
            box.CaptureFocus();
            world.Presenter.Flush();

            Assert.IsTrue(box.IsFocused);
            Assert.AreSame(box, world.Presenter.FocusedTextBox);
        }

        [Test]
        public void Negative_AReleaseBeforeTheUpdate_DropsTheWaitingCapture()
        {
            using RbxGuiTestWorld world = new();
            RbxTextBox box = world.Create<RbxTextBox>("TextBox", world.Screen());
            box.SetVisible(false);
            world.Frame();

            box.SetVisible(true);
            box.CaptureFocus();
            box.ReleaseFocus(false);
            world.Presenter.Flush();

            Assert.IsFalse(box.IsFocused);
            Assert.AreEqual(0, world.Element(box).FocusForTypingCount);
        }

        [Test]
        public void Negative_NothingHeld_SwallowsNothing()
        {
            using RbxGuiTestWorld world = new();
            RbxTextBox box = world.Create<RbxTextBox>("TextBox", world.Screen());
            world.Frame();

            box.CaptureFocus();

            Assert.IsTrue(box.IsFocused);
            Assert.AreEqual(0, world.Element(box).LastFocusHeldKeys.Count, "no probe: an empty key list");
        }

        [Test]
        public void Filter_SwallowsTheOpeningKey_AndTheFirstCharacterWhateverTheLayoutMadeOfIt()
        {
            OpeningKeystrokeFilter filter = new();
            filter.Arm(new[] { KeyT }, 10);

            Assert.IsTrue(filter.ShouldSwallow(KeyT, '\0', 10), "the key down of the opening key");
            Assert.IsTrue(filter.ShouldSwallow(0, '\u0435', 10), "its character on a Russian layout");
            Assert.IsFalse(filter.ShouldSwallow(0, 'x', 10), "twin: the next character is the player's");
            Assert.IsFalse(filter.IsArmed && filter.ShouldSwallow(0, 'y', 11));
        }

        [Test]
        public void Filter_AnEnterThatOpened_IsSwallowed_SoTheEmptyFieldDoesNotSubmit()
        {
            OpeningKeystrokeFilter filter = new();
            filter.Arm(new[] { KeyReturn }, 4);

            Assert.IsTrue(filter.ShouldSwallow(KeyReturn, '\n', 5), "the next frame still counts");
            Assert.IsFalse(filter.ShouldSwallow(0, 'h', 5), "the key carried its character, so typing is free");
        }

        [Test]
        public void Negative_Filter_ExpiresAfterTheNextFrame_AndIsInertWithoutKeys()
        {
            OpeningKeystrokeFilter filter = new();
            filter.Arm(new[] { KeyT }, 10);
            Assert.IsFalse(filter.ShouldSwallow(0, 't', 12), "two frames later a t is typed");
            Assert.IsFalse(filter.IsArmed);

            filter.Arm(new int[0], 20);
            Assert.IsFalse(filter.IsArmed);
            Assert.IsFalse(filter.ShouldSwallow(0, 'a', 20));
        }
    }
}
