using System;
using CoreAI.UiFocus;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// <see cref="ICoreAiUiFocus.HoldsKeyboard"/>: a screen that takes the keyboard while it is open (a Hub over a play
    /// world) holds it without being a text focus, a focused field holds it too, and an implementation written before
    /// the hold answers its text focus. Each with its twin.
    /// </summary>
    public sealed class UiFocusKeyboardHoldEditModeTests
    {
        private sealed class OldFocus : ICoreAiUiFocus
        {
            public bool HasTextFocus { get; set; }

            public bool IsPointerOverUi => false;

            public event Action Changed
            {
                add { }
                remove { }
            }

            public void Report(string surfaceId, bool textFocused, bool pointerOver)
            {
                HasTextFocus = textFocused;
            }

            public void Clear(string surfaceId)
            {
            }
        }

        [Test]
        public void AHold_HoldsTheKeyboard_WithoutBeingATextFocus()
        {
            CoreAiUiFocus focus = new();
            int changes = 0;
            focus.Changed += () => changes++;

            focus.ReportKeyboardHold("hub", true);

            Assert.IsTrue(focus.HoldsKeyboard);
            Assert.IsFalse(focus.HasTextFocus, "no text field has focus");
            Assert.AreEqual(1, changes);

            focus.ReportKeyboardHold("hub", true);
            Assert.AreEqual(1, changes, "the same hold twice changes nothing");

            focus.ReportKeyboardHold("hub", false);
            Assert.IsFalse(focus.HoldsKeyboard);
            Assert.AreEqual(2, changes);
        }

        [Test]
        public void AFocusedField_HoldsTheKeyboardToo()
        {
            CoreAiUiFocus focus = new();

            focus.Report("chat", true, false);

            Assert.IsTrue(focus.HasTextFocus);
            Assert.IsTrue(focus.HoldsKeyboard);
        }

        [Test]
        public void ClearingASurface_ReleasesItsHold_AndLeavesTheOthers()
        {
            CoreAiUiFocus focus = new();
            focus.ReportKeyboardHold("hub", true);
            focus.ReportKeyboardHold("menu", true);

            focus.Clear("hub");
            Assert.IsTrue(focus.HoldsKeyboard, "the menu still holds it");

            focus.Clear("menu");
            Assert.IsFalse(focus.HoldsKeyboard);
        }

        [Test]
        public void AHoldNeedsASurfaceId()
        {
            Assert.Throws<ArgumentException>(() => new CoreAiUiFocus().ReportKeyboardHold(" ", true));
        }

        [Test]
        public void AnImplementationWithoutTheHold_AnswersItsTextFocus()
        {
            ICoreAiUiFocus old = new OldFocus();

            old.ReportKeyboardHold("hub", true);
            Assert.IsFalse(old.HoldsKeyboard, "an old implementation ignores the hold");

            old.Report("chat", true, false);
            Assert.IsTrue(old.HoldsKeyboard, "and still answers a focused field");
        }
    }
}
