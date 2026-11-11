using System;
using System.Collections.Generic;
using CoreAI.RuntimeUi;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using VContainer;

namespace CoreAI.Tests.EditMode.RuntimeUi
{
    /// <summary>
    /// The runtime UI host and its typed C# API: screens from text, live USS and UXML replaces that keep
    /// state, queries, reads and writes, events, ordering and stacking, the theme, the text-focus
    /// signal, teardown without leftovers, the switched-off host and the DI registration.
    /// </summary>
    [TestFixture]
    public sealed class RuntimeUiHostEditModeTests
    {
        private const string HotbarUxml =
            "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n" +
            "  <ui:VisualElement name=\"hotbar\" class=\"hotbar\">\n" +
            "    <ui:Button name=\"slot0\" class=\"slot\" text=\"1\"/>\n" +
            "    <ui:Button name=\"slot1\" class=\"slot\" text=\"2\"/>\n" +
            "    <ui:Button name=\"slot2\" class=\"slot\" text=\"3\"/>\n" +
            "  </ui:VisualElement>\n" +
            "  <ui:Label name=\"counter\" class=\"counter\" text=\"0\"/>\n" +
            "  <ui:Slider name=\"volume\" low-value=\"0\" high-value=\"10\" value=\"2\"/>\n" +
            "  <ui:TextField name=\"chat\"/>\n" +
            "</ui:UXML>";

        private const string HotbarUss =
            ".hotbar { flex-direction: row; } .slot { width: 40px; border-color: rgb(80, 80, 80); } " +
            ".slot.is-selected { border-color: rgb(255, 200, 0); }";

        [Test]
        public void CreateScreen_BuildsStylesAndAnswersQueries()
        {
            using RuntimeUiTestPanel panel = new();

            RuntimeUiResult result = panel.Host.CreateScreen("hud", HotbarUxml, HotbarUss);

            Assert.IsTrue(result.Success, result.ToString());
            Assert.AreEqual(0, result.Diagnostics.Count, string.Join("\n", result.Diagnostics));
            RuntimeUiScreen screen = result.Screen;
            Assert.AreEqual(7, screen.ElementCount);
            Assert.AreSame(panel.Host.LayerRoot, screen.Root.parent.parent);
            RuntimeUiElement first = screen.Query("#hotbar > .slot:enabled");
            Assert.AreEqual("slot0", first.Name);
            List<RuntimeUiElement> slots = new();
            Assert.AreEqual(3, screen.QueryAll(".slot", slots));
            Assert.AreEqual(40f, slots[2].VisualElement.style.width.value.value);
            Assert.AreEqual("2", slots[1].Text);
        }

        [Test]
        public void CreateScreen_DuplicateId_IsRefused()
        {
            using RuntimeUiTestPanel panel = new();
            panel.Create("hud", "<ui:Label/>", null);

            RuntimeUiResult duplicate = panel.Host.CreateScreen("hud", "<ui:Label/>", null);

            Assert.IsFalse(duplicate.Success);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UiScreenExists, duplicate.ErrorCode);
        }

        [Test]
        public void Api_ReadsAndWritesTextValueVisibilityClassesAndStyle()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud", HotbarUxml, HotbarUss);
            RuntimeUiElement counter = screen.GetElement("counter");
            RuntimeUiElement volume = screen.GetElement("volume");
            RuntimeUiElement slot = screen.GetElement("slot1");

            Assert.IsTrue(counter.SetText("42"));
            Assert.IsTrue(volume.SetNumber(7.5f));
            Assert.IsTrue(volume.TryGetValue(out string volumeText));
            Assert.IsTrue(slot.AddClass("is-selected"));
            Assert.IsTrue(counter.SetStyle("color", "rgb(255, 0, 0)"));
            Assert.IsTrue(counter.SetStyle(UssPropertyId.FontSize, UssValue.FromLength(30f)));
            counter.Visible = false;

            Assert.AreEqual("42", ((Label)counter.VisualElement).text);
            Assert.AreEqual("7.5", volumeText);
            Assert.AreEqual(new Color(1f, 200f / 255f, 0f, 1f), slot.VisualElement.style.borderTopColor.value);
            Assert.AreEqual(Color.red, counter.VisualElement.style.color.value);
            Assert.AreEqual(30f, counter.VisualElement.style.fontSize.value.value);
            Assert.AreEqual(DisplayStyle.None, counter.VisualElement.style.display.value);
            Assert.IsFalse(counter.Visible);
            Assert.IsFalse(counter.SetNumber(1f), "A label has no numeric value.");
            Assert.IsFalse(counter.SetStyle("cursor", "link"), "An unsupported property is refused...");
            Assert.AreEqual(1, CountOf(screen.Diagnostics, RuntimeUiDiagnosticCodes.UssUnsupportedProperty),
                "...and reported once.");
            Assert.IsTrue(counter.TryGetResolvedStyle(UssPropertyId.Color, out UssValue resolved));
            Assert.AreEqual(1f, resolved.A);
        }

        [Test]
        public void ReplaceUss_RestylesTheSameTree_AndKeepsTheSelectedSlot()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud", HotbarUxml, HotbarUss);
            RuntimeUiElement slot = screen.GetElement("slot1");
            VisualElement before = slot.VisualElement;
            slot.AddClass("is-selected");
            int buildVersion = screen.BuildVersion;

            RuntimeUiResult result = screen.ReplaceUss(
                ".slot { width: 64px; } .slot.is-selected { border-color: rgb(0, 0, 255); }");

            Assert.IsTrue(result.Success);
            Assert.AreEqual(buildVersion, screen.BuildVersion, "A USS replace must not rebuild the tree.");
            Assert.AreSame(before, slot.VisualElement);
            Assert.IsTrue(slot.HasClass("is-selected"));
            Assert.AreEqual(64f, before.style.width.value.value);
            Assert.AreEqual(Color.blue, before.style.borderTopColor.value);
        }

        [Test]
        public void ReplaceUxml_KeepsValuesAndHandlesOfMatchingNames_AndDetachesTheRest()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud", HotbarUxml, HotbarUss);
            RuntimeUiElement volume = screen.GetElement("volume");
            RuntimeUiElement slot = screen.GetElement("slot1");
            RuntimeUiElement slot2 = screen.GetElement("slot2");
            RuntimeUiElement chat = screen.GetElement("chat");
            ((Slider)volume.VisualElement).SetValueWithoutNotify(9f);
            ((TextField)chat.VisualElement).SetValueWithoutNotify("hello");
            slot.AddClass("is-selected");
            slot.SetText("A");
            int clicks = 0;
            slot.Clicked += _ => clicks++;

            RuntimeUiResult result = screen.ReplaceUxml(
                "<ui:VisualElement name=\"hotbar\" class=\"hotbar\">" +
                "<ui:Button name=\"slot0\" class=\"slot\"/><ui:Button name=\"slot1\" class=\"slot\"/>" +
                "</ui:VisualElement><ui:Slider name=\"volume\" high-value=\"10\"/><ui:TextField name=\"chat\"/>");

            Assert.IsTrue(result.Success, result.ToString());
            Assert.IsTrue(volume.IsAlive);
            Assert.IsTrue(volume.TryGetNumber(out float kept));
            Assert.AreEqual(9f, kept, "The slider keeps the value the player set.");
            Assert.AreEqual("hello", chat.Text);
            Assert.IsTrue(slot.IsAlive);
            Assert.IsTrue(slot.HasClass("is-selected"), "Classes set through the handle are replayed.");
            Assert.AreEqual("A", slot.Text, "Text set through the handle is replayed.");
            Assert.AreEqual(1, slot.SubscriberCount, "Subscribers survive the rebuild.");
            Assert.IsTrue(slot.HasRegisteredEvents, "...and are wired to the new element.");
            Assert.IsFalse(slot2.IsAlive, "slot2 is gone from the markup.");
            Assert.IsFalse(slot2.SetText("x"));
        }

        [Test]
        public void ShowHideSortOrderAndStack_OrderTheScreenLayers()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen hud = panel.Create("hud", "<ui:Label/>", null);
            RuntimeUiScreen menu = panel.Create("menu", "<ui:Label/>", null);
            RuntimeUiScreen dialog = panel.Create("dialog", "<ui:Label/>", null);
            VisualElement layer = panel.Host.LayerRoot;

            hud.SetSortOrder(10);
            Assert.AreSame(hud.Root.parent, layer[layer.childCount - 1], "Higher sort order draws on top.");

            hud.SetSortOrder(0);
            panel.Host.PushScreen(menu);
            panel.Host.PushScreen(dialog, true);
            Assert.IsFalse(menu.IsVisible, "Pushing with hidePrevious hides the previous top.");
            Assert.AreSame(dialog.Root.parent, layer[layer.childCount - 1]);

            Assert.AreSame(dialog, panel.Host.PopScreen());
            Assert.IsFalse(dialog.IsVisible);
            Assert.IsTrue(menu.IsVisible, "Popping re-shows the screen the push hid.");

            hud.Hide();
            Assert.AreEqual(DisplayStyle.None, hud.Root.parent.style.display.value);
            hud.Show();
            Assert.AreEqual(DisplayStyle.Flex, hud.Root.parent.style.display.value);
        }

        [Test]
        public void Destroy_LeavesNoElementsCallbacksOrSubscribers()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud", HotbarUxml, HotbarUss + " .slot:hover { opacity: 1; }");
            RuntimeUiElement slot = screen.GetElement("slot0");
            slot.Clicked += _ => { };
            slot.PointerEntered += _ => { };
            screen.GetElement("volume").ValueChanged += _ => { };
            RuntimeUiScreen destroyed = null;
            panel.Host.ScreenDestroyed += s => destroyed = s;
            Assert.Greater(screen.LiveCallbackCount, 0);
            Assert.AreEqual(3, screen.LiveSubscriberCount);

            Assert.IsTrue(panel.Host.DestroyScreen("hud"));

            Assert.IsTrue(screen.IsDestroyed);
            Assert.AreSame(screen, destroyed);
            Assert.AreEqual(0, screen.LiveCallbackCount, "Zero state callbacks remain.");
            Assert.AreEqual(0, screen.LiveSubscriberCount, "Zero subscribers remain.");
            Assert.AreEqual(0, screen.LiveHandleRegistrationCount, "Zero UI Toolkit registrations remain.");
            Assert.IsFalse(slot.IsAlive);
            Assert.AreEqual(0, slot.SubscriberCount, "The handle drops its subscribers.");
            Assert.IsFalse(slot.HasRegisteredEvents);
            Assert.IsNull(screen.Root.parent?.parent, "The screen layer left the host layer.");
            Assert.AreEqual(0, screen.Root.childCount);
            Assert.AreEqual(0, panel.Host.Screens.Count);
            Assert.IsFalse(screen.ReplaceUss(".a { color: red; }").Success);
        }

        [Test]
        public void DestroyScreensOwnedBy_RemovesOnlyThatOwnersScreens()
        {
            using RuntimeUiTestPanel panel = new();
            panel.Host.CreateScreen("a", null, null, new RuntimeUiScreenOptions { Owner = "mod.one" });
            panel.Host.CreateScreen("b", null, null, new RuntimeUiScreenOptions { Owner = "mod.one" });
            panel.Host.CreateScreen("c", null, null, new RuntimeUiScreenOptions { Owner = "mod.two" });

            Assert.AreEqual(2, panel.Host.DestroyScreensOwnedBy("mod.one"));

            Assert.AreEqual(1, panel.Host.Screens.Count);
            Assert.AreEqual("c", panel.Host.Screens[0].Id);
        }

        [Test]
        public void ThemeToken_ChangesRestyleEveryScreen_AndAScreenOverrideKeepsItsOwnValue()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen plain = panel.Create("plain", "<ui:Label name=\"l\"/>", "Label { color: var(--cai-color-accent); }");
            RuntimeUiScreen own = panel.Create("own", "<ui:Label name=\"l\"/>",
                ":root { --cai-color-accent: rgb(0, 255, 0); } Label { color: var(--cai-color-accent); }");

            panel.Host.Theme.SetToken("--cai-color-accent", "rgb(0, 0, 255)");

            Assert.AreEqual(Color.blue, plain.GetElement("l").VisualElement.style.color.value);
            Assert.AreEqual(Color.green, own.GetElement("l").VisualElement.style.color.value);
        }

        [Test]
        public void TextFieldFocus_RaisesTheTextInputSignal_AndHidingTheScreenReleasesIt()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud", HotbarUxml, HotbarUss);
            List<bool> changes = new();
            panel.Host.TextInputFocusChanged += changes.Add;
            IRuntimeUiTextInputFocus signal = panel.Host;

            screen.GetElement("chat").Focus();
            Assert.IsTrue(signal.IsTextInputFocused, "Focusing the text field must raise the typing signal.");

            screen.GetElement("slot0").Focus();
            Assert.IsFalse(signal.IsTextInputFocused, "A focused button is not typing.");

            screen.GetElement("chat").Focus();
            screen.Hide();
            Assert.IsFalse(signal.IsTextInputFocused, "Hiding the screen releases the field it holds.");
            CollectionAssert.AreEqual(new[] { true, false, true, false }, changes);
        }

        [Test]
        public void Events_ClickedValueChangedSubmitted_ReachSubscribers_AndAThrowingHandlerIsContained()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("hud",
                "<ui:Button name=\"b\"/><ui:Toggle name=\"t\"/><ui:TextField name=\"f\"/>", null);
            int clicked = 0;
            int changed = 0;
            int submitted = 0;
            RuntimeUiElement button = screen.GetElement("b");
            button.Clicked += _ => throw new InvalidOperationException("handler bug");
            button.Clicked += _ => clicked++;
            screen.GetElement("t").ValueChanged += _ => changed++;
            screen.GetElement("f").Submitted += _ => submitted++;

            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button.VisualElement;
                button.VisualElement.SendEvent(submit);
            }

            ((Toggle)screen.GetElement("t").VisualElement).value = true;
            using (KeyDownEvent enter = KeyDownEvent.GetPooled('\n', KeyCode.Return, EventModifiers.None))
            {
                enter.target = screen.GetElement("f").VisualElement;
                screen.GetElement("f").VisualElement.SendEvent(enter);
            }

            Assert.AreEqual(1, clicked, "The second handler still runs after the first one threw.");
            Assert.AreEqual(1, changed);
            Assert.AreEqual(1, submitted);
        }

        [Test]
        public void CreateElement_WithoutMarkup_BuildsAbsoluteAndFlexLayoutsAndZOrder()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("gui", null, ".frame { background-color: rgb(10, 10, 10); }");
            RuntimeUiElement frame = screen.CreateElement(RuntimeUiElementKind.VisualElement, null, "frame");
            frame.AddClass("frame");
            frame.SetStyle(UssPropertyId.Position, UssValue.FromKeyword("absolute"));
            frame.SetStyle(UssPropertyId.Left, UssValue.FromLength(50f, UssUnit.Percent));
            frame.SetStyle(UssPropertyId.MarginLeft, UssValue.FromLength(-20f));
            frame.SetStyle(UssPropertyId.Translate, UssValue.FromPair(-50f, UssUnit.Percent, 0f, UssUnit.Pixel));
            RuntimeUiElement row = frame.CreateChild(RuntimeUiElementKind.VisualElement, "row");
            row.SetStyle("flex-direction", "row");
            RuntimeUiElement back = row.CreateChild(RuntimeUiElementKind.Label, "back");
            RuntimeUiElement front = row.CreateChild(RuntimeUiElementKind.Button, "front");

            back.SetZOrder(5);

            Assert.AreEqual(4, screen.ElementCount);
            Assert.AreEqual(new Color(10f / 255f, 10f / 255f, 10f / 255f, 1f), frame.VisualElement.style.backgroundColor.value,
                "API-created elements are styled by the screen's USS like markup elements.");
            Assert.AreEqual(Position.Absolute, frame.VisualElement.style.position.value);
            Assert.AreEqual(FlexDirection.Row, row.VisualElement.style.flexDirection.value);
            Assert.AreEqual(1, row.VisualElement.IndexOf(back.VisualElement), "A higher z-order draws on top.");
            Assert.AreEqual(0, row.VisualElement.IndexOf(front.VisualElement));
            Assert.AreSame(frame, row.Parent);
            Assert.IsTrue(back.Remove());
            Assert.IsFalse(back.IsAlive);
            Assert.AreEqual(3, screen.ElementCount);
        }

        [Test]
        public void ListView_RowsAreCreatedStyledAndBoundToTheItems()
        {
            using RuntimeUiTestPanel panel = new();
            RuntimeUiScreen screen = panel.Create("inv", "<ui:ListView name=\"items\" fixed-item-height=\"20\"/>",
                ".cai-list-item { color: rgb(0, 255, 0); }");
            RuntimeUiElement list = screen.GetElement("items");

            Assert.IsTrue(list.SetItems(new[] { "Sword", "Shield" }));
            ListView view = (ListView)list.VisualElement;
            VisualElement row = view.makeItem();
            view.bindItem(row, 1);

            Assert.AreEqual(2, view.itemsSource.Count);
            Assert.IsTrue(row.ClassListContains(RuntimeUiScreen.ListItemClass));
            Assert.AreEqual("Shield", ((Label)row).text);
            Assert.AreEqual(Color.green, row.style.color.value);
        }

        [Test]
        public void Limits_TooLargeTextAndTooManyElements_AreRefusedWithCodes()
        {
            using RuntimeUiTestPanel panel = new(new RuntimeUiHostOptions
            {
                MaxElementsPerScreen = 3,
                MaxScreens = 1,
                ParseLimits = new RuntimeUiParseLimits { MaxTextLength = 200 }
            });

            RuntimeUiResult tooLarge = panel.Host.CreateScreen("a", new string(' ', 201), null);
            RuntimeUiResult tooMany = panel.Host.CreateScreen("a", "<ui:Label/><ui:Label/><ui:Label/><ui:Label/>", null);
            RuntimeUiScreen ok = panel.Create("a", "<ui:Label/>", null);
            RuntimeUiResult tooManyScreens = panel.Host.CreateScreen("b", null, null);

            Assert.AreEqual(RuntimeUiDiagnosticCodes.UiTextTooLarge, tooLarge.ErrorCode);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UiElementLimit, tooMany.ErrorCode);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UiScreenLimit, tooManyScreens.ErrorCode);
            ok.CreateElement(RuntimeUiElementKind.Label);
            ok.CreateElement(RuntimeUiElementKind.Label);
            Assert.IsNull(ok.CreateElement(RuntimeUiElementKind.Label), "The fourth element exceeds the limit.");
            Assert.AreEqual(1, CountOf(ok.Diagnostics, RuntimeUiDiagnosticCodes.UiElementLimit));
        }

        [Test]
        public void UnsupportedRule_ReportsOnceWithItsLine_AndTheSupportedTwinReportsNothing()
        {
            using RuntimeUiTestPanel panel = new();

            RuntimeUiResult unsupported = panel.Host.CreateScreen("a", HotbarUxml, ".slot {\n  cursor: link;\n}");
            RuntimeUiResult supported = panel.Host.CreateScreen("b", HotbarUxml, ".slot {\n  opacity: 1;\n}");

            Assert.AreEqual(1, unsupported.Diagnostics.Count, string.Join("\n", unsupported.Diagnostics));
            Assert.AreEqual(2, unsupported.Diagnostics[0].Line);
            Assert.AreEqual(0, supported.Diagnostics.Count, string.Join("\n", supported.Diagnostics));
        }

        [Test]
        public void AutoCreatedSurface_IsBuiltOnFirstUse_AndDisposedWithTheHost()
        {
            RuntimeUiHost host = new(new RuntimeUiHostOptions { PersistAcrossScenes = false });
            try
            {
                Assert.IsNull(host.LayerRoot, "Nothing is created before the first screen.");

                host.CreateScreen("hud", "<ui:Label/>", null);

                Assert.IsNotNull(host.LayerRoot, "A fresh world gets a panel without any setup.");
                Assert.IsNotNull(GameObject.Find("CoreAI Runtime UI"));
            }
            finally
            {
                host.Dispose();
            }

            Assert.IsNull(GameObject.Find("CoreAI Runtime UI"));
        }

        [Test]
        public void DisabledHost_RefusesWithUiDisabled()
        {
            IRuntimeUiHost host = new DisabledRuntimeUiHost();

            RuntimeUiResult result = host.CreateScreen("hud", "<ui:Label/>", null);

            Assert.IsFalse(host.IsEnabled);
            Assert.IsFalse(result.Success);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UiDisabled, result.ErrorCode);
            Assert.IsFalse(host.IsTextInputFocused);
        }

        [Test]
        public void Installer_RegistersOneHostForBothContracts()
        {
            ContainerBuilder builder = new();
            builder.RegisterRuntimeUi(new RuntimeUiHostOptions { AutoCreateSurface = false });
            IObjectResolver container = builder.Build();
            try
            {
                IRuntimeUiHost host = container.Resolve<IRuntimeUiHost>();
                Assert.IsInstanceOf<RuntimeUiHost>(host);
                Assert.AreSame(host, container.Resolve<IRuntimeUiTextInputFocus>());
            }
            finally
            {
                container.Dispose();
            }
        }

        [Test]
        public void Installer_Disabled_RegistersTheSwitchedOffHost()
        {
            ContainerBuilder builder = new();
            builder.RegisterRuntimeUi(new RuntimeUiHostOptions { Enabled = false });
            IObjectResolver container = builder.Build();
            try
            {
                Assert.IsInstanceOf<DisabledRuntimeUiHost>(container.Resolve<IRuntimeUiHost>());
            }
            finally
            {
                container.Dispose();
            }
        }

        [Test]
        public void HostGame_ReplacesTheHostWithItsOwn()
        {
            ContainerBuilder builder = new();
            builder.RegisterRuntimeUi(new RuntimeUiHostOptions { AutoCreateSurface = false });
            builder.Register<DisabledRuntimeUiHost>(Lifetime.Singleton).As<IRuntimeUiHost>();
            IObjectResolver container = builder.Build();
            try
            {
                Assert.IsInstanceOf<DisabledRuntimeUiHost>(container.Resolve<IRuntimeUiHost>(),
                    "The host game's registration, made after RegisterRuntimeUi, wins.");
            }
            finally
            {
                container.Dispose();
            }
        }

        private static int CountOf(IReadOnlyList<RuntimeUiDiagnostic> diagnostics, string code)
        {
            int count = 0;
            for (int i = 0; i < diagnostics.Count; i++)
            {
                if (diagnostics[i].Code == code)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
