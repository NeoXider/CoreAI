using CoreAI.RuntimeUi;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode.RuntimeUi
{
    /// <summary>
    /// The style applier and the screen's state tracking: cascade results land on UI Toolkit inline
    /// styles, properties that stop applying are reset, and hover/active/focus/disabled/checked rules
    /// follow the element's state as it changes.
    /// </summary>
    [TestFixture]
    public sealed class RuntimeUiStyleApplierEditModeTests
    {
        [Test]
        public void Apply_TypedValues_LandOnInlineStyles()
        {
            RuntimeUiStyleApplier applier = new(null);
            VisualElement element = new();

            Assert.IsTrue(applier.Apply(element.style, UssPropertyId.Width, UssValue.FromLength(50f, UssUnit.Percent), out _));
            Assert.IsTrue(applier.Apply(element.style, UssPropertyId.Position, UssValue.FromKeyword("absolute"), out _));
            Assert.IsTrue(applier.Apply(element.style, UssPropertyId.BackgroundColor, UssValue.FromColor(1f, 0f, 0f, 0.5f), out _));
            Assert.IsTrue(applier.Apply(element.style, UssPropertyId.Translate,
                UssValue.FromPair(-50f, UssUnit.Percent, 4f, UssUnit.Pixel), out _));
            Assert.IsTrue(applier.Apply(element.style, UssPropertyId.Height, UssValue.FromKeyword("auto"), out _));

            Assert.AreEqual(new Length(50f, LengthUnit.Percent), element.style.width.value);
            Assert.AreEqual(Position.Absolute, element.style.position.value);
            Assert.AreEqual(new Color(1f, 0f, 0f, 0.5f), element.style.backgroundColor.value);
            Assert.AreEqual(new Length(-50f, LengthUnit.Percent), element.style.translate.value.x);
            Assert.AreEqual(StyleKeyword.Auto, element.style.height.keyword);
        }

        [Test]
        public void Apply_UnknownImage_IsRefusedWithAReason()
        {
            RuntimeUiStyleApplier applier = new(new RuntimeUiAssetRegistry(false));
            VisualElement element = new();

            bool applied = applier.Apply(element.style, UssPropertyId.BackgroundImage,
                UssValue.FromAsset("Icons/missing", true), out string error);

            Assert.IsFalse(applied);
            StringAssert.Contains("Icons/missing", error);
        }

        [Test]
        public void Screen_RuleThatStopsMatching_ResetsItsInlineProperty()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud", "<ui:Label name=\"a\" class=\"big\"/>",
                ".big { font-size: 40px; width: 10px; }");
            RuntimeUiElement label = screen.GetElement("a");
            Assert.AreEqual(40f, label.VisualElement.style.fontSize.value.value);

            label.RemoveClass("big");

            Assert.AreEqual(StyleKeyword.Null, label.VisualElement.style.fontSize.keyword,
                "A property no rule sets any more must not linger as an inline value.");
            Assert.AreEqual(StyleKeyword.Null, label.VisualElement.style.width.keyword);
        }

        [Test]
        public void Hover_Active_Focus_RestyleAndRevert()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud", "<ui:Button name=\"b\" class=\"slot\"/>",
                ".slot { opacity: 0.5; } .slot:hover { opacity: 0.8; } .slot:active { opacity: 1; } " +
                ".slot:focus { width: 33px; }");
            RuntimeUiElement button = screen.GetElement("b");
            VisualElement element = button.VisualElement;

            Assert.AreEqual(0.5f, element.style.opacity.value, 1e-5f);
            screen.SetPseudoStateForTesting(button, UssPseudoState.Hover, true);
            Assert.AreEqual(0.8f, element.style.opacity.value, 1e-5f);
            screen.SetPseudoStateForTesting(button, UssPseudoState.Active, true);
            Assert.AreEqual(1f, element.style.opacity.value, 1e-5f);
            screen.SetPseudoStateForTesting(button, UssPseudoState.Active, false);
            screen.SetPseudoStateForTesting(button, UssPseudoState.Hover, false);
            Assert.AreEqual(0.5f, element.style.opacity.value, 1e-5f);
            screen.SetPseudoStateForTesting(button, UssPseudoState.Focus, true);
            Assert.AreEqual(33f, element.style.width.value.value);
            screen.SetPseudoStateForTesting(button, UssPseudoState.Focus, false);
            Assert.AreEqual(StyleKeyword.Null, element.style.width.keyword);
        }

        [Test]
        public void HoverOnAParent_RestylesTheChild()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud",
                "<ui:VisualElement name=\"slot\" class=\"slot\"><ui:VisualElement name=\"icon\" class=\"icon\"/></ui:VisualElement>",
                ".slot:hover > .icon { scale: 1.2; }");
            RuntimeUiElement slot = screen.GetElement("slot");
            VisualElement icon = screen.GetElement("icon").VisualElement;

            screen.SetPseudoStateForTesting(slot, UssPseudoState.Hover, true);

            Assert.AreEqual(new Vector3(1.2f, 1.2f, 1f), icon.style.scale.value.value);
        }

        [Test]
        public void Disabled_FollowsSetEnabled_ForTheWholeSubtree()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud",
                "<ui:VisualElement name=\"row\"><ui:Button name=\"b\"/></ui:VisualElement>",
                "Button:disabled { opacity: 0.3; } Button:enabled { opacity: 1; }");
            RuntimeUiElement row = screen.GetElement("row");
            VisualElement button = screen.GetElement("b").VisualElement;
            Assert.AreEqual(1f, button.style.opacity.value, 1e-5f);

            row.SetEnabled(false);
            Assert.AreEqual(0.3f, button.style.opacity.value, 1e-5f);
            row.SetEnabled(true);
            Assert.AreEqual(1f, button.style.opacity.value, 1e-5f);
        }

        [Test]
        public void Checked_FollowsTheToggleValue()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud", "<ui:Toggle name=\"t\"/>",
                "Toggle { opacity: 0.4; } Toggle:checked { opacity: 0.9; }");
            RuntimeUiElement toggle = screen.GetElement("t");

            Assert.AreEqual(0.4f, toggle.VisualElement.style.opacity.value, 1e-5f);
            toggle.SetBool(true);
            Assert.AreEqual(0.9f, toggle.VisualElement.style.opacity.value, 1e-5f);
        }

        [Test]
        public void OnlyElementsSomeSelectorTestsForState_RegisterStateCallbacks()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen tracked = panel.Create("a", "<ui:Button class=\"slot\"/><ui:Label/>", ".slot:hover { opacity: 1; }");
            RuntimeUiScreen untracked = panel.Create("b", "<ui:Button class=\"slot\"/><ui:Label/>", ".slot { opacity: 1; }");

            Assert.AreEqual(2, tracked.LiveCallbackCount, "Pointer enter and leave on the one .slot element.");
            Assert.AreEqual(0, untracked.LiveCallbackCount, "Without a state selector nothing listens.");
        }
    }
}
