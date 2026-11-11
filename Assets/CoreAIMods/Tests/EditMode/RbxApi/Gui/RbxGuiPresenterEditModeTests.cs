using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Gui
{
    /// <summary>
    /// The mapping from Roblox GUI instances to the runtime UI port, engine-free over a fake host: which
    /// ScreenGuis become screens, which element each GUI object becomes, the typed styles a property writes,
    /// what an equal write and an idle frame push (nothing), removal, the shared per-mod budgets, and the
    /// signals the host's events turn into. Each behaviour has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class RbxGuiPresenterEditModeTests
    {
        private RbxGuiTestWorld _world;

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
        }

        private RbxGuiTestWorld World(RbxUiBudgets budgets = null)
        {
            _world = new RbxGuiTestWorld(budgets);
            return _world;
        }

        [Test]
        public void MVP15_ScreenGuiInTheLocalPlayerGui_IsOneHostScreenOwnedByItsMod()
        {
            RbxGuiTestWorld world = World();
            RbxScreenGui screen = world.Screen();
            screen.SetDisplayOrder(7);
            world.Frame();

            FakeRbxUiBackend.FakeScreen host = world.HostScreen(screen);
            Assert.IsNotNull(host);
            Assert.AreEqual(RbxGuiTestWorld.Mod, host.Owner);
            Assert.AreEqual(7, host.SortOrder, "DisplayOrder is the screen's sort order");
            Assert.IsTrue(host.IsVisible);
            StringAssert.StartsWith("rbxgui/", host.HostId);
            Assert.AreEqual(RbxGuiPresenter.ScreenUss, host.UssText);
            Assert.AreEqual(1, world.World.CountScreens(RbxGuiTestWorld.Mod),
                "a ScreenGui counts against the mod's screen budget like a UxmlService screen");

            screen.SetEnabled(false);
            world.Frame();
            Assert.IsFalse(host.IsVisible, "Enabled = false hides the screen");
        }

        [Test]
        public void MVP15_ScreenGuiElsewhere_IsNotShown_NegativeTwin()
        {
            RbxGuiTestWorld world = World();
            RbxPlayer other = world.Players.EnsureActor(world.Registry, "other-player");
            RbxScreenGui otherScreen = world.Create<RbxScreenGui>("ScreenGui", other.FindFirstChildOfClass("PlayerGui"));
            RbxScreenGui starter = world.Create<RbxScreenGui>("ScreenGui", world.Game.GetService("StarterGui"));
            RbxFrame holder = world.Create<RbxFrame>("Frame", world.Screen());
            RbxScreenGui nested = world.Create<RbxScreenGui>("ScreenGui", holder);
            world.Frame();

            Assert.IsNull(world.HostScreen(otherScreen), "another player's PlayerGui is drawn on their screen");
            Assert.IsNull(world.HostScreen(starter), "StarterGui is a template, not a screen");
            Assert.IsNull(world.HostScreen(nested), "a ScreenGui inside a GuiObject is not drawn");
            Assert.AreEqual(1, world.Presenter.ScreenCount);
        }

        [Test]
        public void MVP15_AFrame_IsAnAbsolutelyPlacedElement_WithItsBackgroundAndBorder()
        {
            RbxGuiTestWorld world = World();
            RbxScreenGui screen = world.Screen();
            RbxFrame frame = world.Create<RbxFrame>("Frame");
            frame.SetPosition(new RbxUDim2(0f, 100, 0f, 50));
            frame.SetBackgroundColor3(new RbxColor3(1f, 0f, 0f));
            frame.SetBackgroundTransparency(0.25d);
            frame.Parent = screen;
            world.Frame();

            FakeRbxUiBackend.FakeElement element = world.Element(frame);
            Assert.AreEqual("VisualElement", element.Kind);
            Assert.IsTrue(element.Classes.Contains("rbx-gui"));
            Assert.AreEqual(RbxUiStyleValue.Pixels(99f).ToString(), element.Typed(RbxUiStyleProperty.Left).ToString(),
                "the default one-pixel border lies outside the box (BorderMode Outline)");
            Assert.AreEqual(RbxUiStyleValue.Pixels(102f).ToString(), element.Typed(RbxUiStyleProperty.Width).ToString());
            Assert.AreEqual(RbxUiStyleValue.Pixels(1f).ToString(),
                element.Typed(RbxUiStyleProperty.BorderLeftWidth).ToString());
            RbxUiStyleValue background = element.Typed(RbxUiStyleProperty.BackgroundColor);
            Assert.AreEqual(1f, background.A);
            Assert.AreEqual(0.75f, background.D, 1e-6f, "alpha is 1 - BackgroundTransparency");
            Assert.AreEqual("Ignore", element.Attributes["picking-mode"],
                "an inactive Frame lets clicks through to the world");

            frame.SetActive(true);
            frame.SetBorderSizePixel(0);
            world.Frame();
            Assert.AreEqual("Position", element.Attributes["picking-mode"], "twin: Active sinks input");
            Assert.AreEqual(RbxUiStyleValue.Pixels(100f).ToString(), element.Typed(RbxUiStyleProperty.Left).ToString());
            Assert.AreEqual(RbxUiStyleValue.Pixels(0f).ToString(),
                element.Typed(RbxUiStyleProperty.BorderLeftWidth).ToString());
        }

        [Test]
        public void MVP15_AChildElement_IsPlacedRelativeToItsParentElement()
        {
            RbxGuiTestWorld world = World();
            RbxScreenGui screen = world.Screen();
            RbxFrame parent = world.Create<RbxFrame>("Frame");
            parent.SetPosition(new RbxUDim2(0f, 200, 0f, 100));
            parent.SetBorderSizePixel(0);
            parent.Parent = screen;
            RbxFrame child = world.Create<RbxFrame>("Frame");
            child.SetPosition(new RbxUDim2(0f, 10, 0f, 20));
            child.SetBorderSizePixel(0);
            child.Parent = parent;
            world.Frame();

            FakeRbxUiBackend.FakeElement element = world.Element(child);
            Assert.AreSame(world.Element(parent), element.Parent, "the element tree mirrors the instance tree");
            Assert.AreEqual(RbxUiStyleValue.Pixels(10f).ToString(), element.Typed(RbxUiStyleProperty.Left).ToString());
            Assert.AreEqual(new RbxVector2(210f, 120f), child.AbsolutePosition);
        }

        [Test]
        public void MVP15_ATextLabel_PushesItsTextAndTextStyle()
        {
            RbxGuiTestWorld world = World();
            RbxTextLabel label = world.Create<RbxTextLabel>("TextLabel");
            label.TextState.SetText("Score: 0");
            label.TextState.SetTextSize(24d);
            label.TextState.SetTextColor3(new RbxColor3(1f, 1f, 1f));
            label.TextState.SetTextXAlignment(RbxTextXAlignment.Left);
            label.TextState.SetFont(19);
            label.Parent = world.Screen();
            world.Frame();

            FakeRbxUiBackend.FakeElement element = world.Element(label);
            Assert.AreEqual("Label", element.Kind);
            Assert.AreEqual("Score: 0", element.Text);
            Assert.AreEqual("24px", element.Typed(RbxUiStyleProperty.FontSize).ToString());
            Assert.AreEqual(RbxUiKeywords.MiddleLeft, element.Typed(RbxUiStyleProperty.UnityTextAlign).Keyword);
            Assert.AreEqual(RbxUiKeywords.Bold, element.Typed(RbxUiStyleProperty.UnityFontStyle).Keyword,
                "GothamBold draws bold");
            Assert.AreEqual(RbxUiKeywords.NoWrap, element.Typed(RbxUiStyleProperty.WhiteSpace).Keyword);
            Assert.AreEqual("false", element.Attributes["enable-rich-text"], "RichText is off by default");

            label.TextState.SetTextScaled(true);
            world.Frame();
            Assert.AreEqual(RbxGuiTextMetrics.FitTextSize("Score: 0", 200f, 50f, false, 1f).ToString() + "px",
                element.Typed(RbxUiStyleProperty.FontSize).ToString(), "TextScaled fits the size to the box");
        }

        [Test]
        public void MVP15_AnEqualWriteAndAnIdleFrame_PushNothing()
        {
            RbxGuiTestWorld world = World();
            RbxTextLabel label = world.Create<RbxTextLabel>("TextLabel", world.Screen());
            world.Frame();
            FakeRbxUiBackend.FakeElement element = world.Element(label);
            int writes = element.TypedStyleWrites;

            world.Frame();
            label.TextState.SetText(label.TextState.Text);
            label.SetSize(label.Size);
            world.Frame();
            Assert.AreEqual(writes, element.TypedStyleWrites, "nothing changed, nothing is pushed");

            label.TextState.SetTextColor3(new RbxColor3(0f, 1f, 0f));
            world.Frame();
            Assert.AreEqual(writes + 1, element.TypedStyleWrites, "twin: one real change pushes one style");
        }

        [Test]
        public void MVP15_ZIndexAndVisible_ReachTheElement()
        {
            RbxGuiTestWorld world = World();
            RbxFrame frame = world.Create<RbxFrame>("Frame", world.Screen());
            frame.SetZIndex(5);
            frame.SetVisible(false);
            world.Frame();

            FakeRbxUiBackend.FakeElement element = world.Element(frame);
            Assert.AreEqual(5, element.ZOrder, "ZIndex orders siblings");
            Assert.IsFalse(element.Visible);
            frame.SetVisible(true);
            world.Frame();
            Assert.IsTrue(element.Visible);
        }

        [Test]
        public void MVP15_DestroyingOrMovingOut_RemovesTheScreenAndTheElements()
        {
            RbxGuiTestWorld world = World();
            RbxScreenGui screen = world.Screen();
            RbxFrame frame = world.Create<RbxFrame>("Frame", screen);
            RbxTextLabel label = world.Create<RbxTextLabel>("TextLabel", frame);
            world.Frame();
            FakeRbxUiBackend.FakeElement labelElement = world.Element(label);
            Assert.AreEqual(2, world.Presenter.ElementCount);

            label.Parent = null;
            world.Frame();
            Assert.IsFalse(labelElement.IsAlive, "a GuiObject taken out of the screen leaves it");
            Assert.AreEqual(1, world.World.CountElements(RbxGuiTestWorld.Mod));

            FakeRbxUiBackend.FakeScreen host = world.HostScreen(screen);
            screen.Destroy();
            world.Frame();
            Assert.IsTrue(host.IsDestroyed);
            Assert.AreEqual(0, world.Presenter.ScreenCount);
            Assert.AreEqual(0, world.World.CountScreens(RbxGuiTestWorld.Mod));
            Assert.AreEqual(0, world.World.CountElements(RbxGuiTestWorld.Mod));
            Assert.AreEqual(0, world.Backend.Screens.Count);
        }

        [Test]
        public void MVP15_Budgets_AreSharedWithUxmlServiceScreens()
        {
            RbxGuiTestWorld world = World(new RbxUiBudgets { MaxScreensPerMod = 1, MaxElementsPerMod = 2 });
            RbxScreenGui first = world.Screen();
            RbxScreenGui second = world.Screen();
            world.Frame();

            Assert.IsNotNull(world.HostScreen(first));
            Assert.IsNull(world.HostScreen(second), "the second screen is over the budget of one");
            Assert.AreEqual(1, world.Log.Count, "and it is reported once");

            RbxError refused = Assert.Throws<RbxError>(() => world.Presenter.RequireRoomForMove(
                world.Create<RbxScreenGui>("ScreenGui"), world.PlayerGui));
            StringAssert.Contains(RbxUiErrors.BudgetScreens, refused.Message);

            for (int i = 0; i < 3; i++)
            {
                world.Create<RbxFrame>("Frame", first);
            }

            world.Frame();
            Assert.AreEqual(2, world.Presenter.ElementCount, "the third frame is over the element budget");
        }

        [Test]
        public void MVP15_WriteBudget_IsChargedPerWriteOfAShownObject()
        {
            RbxGuiTestWorld world = World(new RbxUiBudgets { MaxWritesPerFrame = 3 });
            RbxFrame frame = world.Create<RbxFrame>("Frame", world.Screen());
            RbxFrame hidden = world.Create<RbxFrame>("Frame");
            world.Frame();

            frame.SetZIndex(2);
            frame.SetZIndex(3);
            frame.SetZIndex(4);
            RbxError refused = Assert.Throws<RbxError>(() => frame.SetZIndex(5));
            StringAssert.Contains(RbxUiErrors.BudgetWrites, refused.Message);
            Assert.AreEqual(4, frame.ZIndex, "the refused write did not happen");
            for (int i = 0; i < 10; i++)
            {
                hidden.SetZIndex(i);
            }

            world.Frame();
            Assert.DoesNotThrow(() => frame.SetZIndex(5), "the budget is per frame");
        }

        [Test]
        public void MVP15_AClick_FiresMouseButton1Click_AndActivatedWhileActive()
        {
            RbxGuiTestWorld world = World();
            RbxTextButton button = world.Create<RbxTextButton>("TextButton", world.Screen());
            List<object[]> clicks = world.Listen(button.MouseButton1Click);
            List<object[]> activations = world.Listen(button.Activated);
            world.Frame();

            world.Element(button).ClickByPlayer();
            world.Frame();
            Assert.AreEqual(1, clicks.Count);
            Assert.AreEqual(1, activations.Count);
            Assert.AreEqual(1, activations[0][1], "clickCount is 1");
            Assert.AreEqual("Button", world.Element(button).Kind);
            Assert.AreEqual("Position", world.Element(button).Attributes["picking-mode"]);

            button.SetActive(false);
            world.Frame();
            world.Element(button).ClickByPlayer();
            world.Frame();
            Assert.AreEqual(2, clicks.Count, "MouseButton1Click fires whatever Active is");
            Assert.AreEqual(1, activations.Count, "twin: Activated does not while Active is false");
        }

        [Test]
        public void MVP15_ThePointer_FiresMouseEnterAndMouseLeave_WithItsPosition()
        {
            RbxGuiTestWorld world = World();
            RbxFrame frame = world.Create<RbxFrame>("Frame", world.Screen());
            List<object[]> entered = world.Listen(frame.MouseEnter);
            List<object[]> left = world.Listen(frame.MouseLeave);
            frame.NotePointerInterest();
            world.Frame();
            Assert.AreEqual("Position", world.Element(frame).Attributes["picking-mode"],
                "an object a script hovers takes the pointer");

            world.Element(frame).EnterByPlayer();
            world.Element(frame).ExitByPlayer();
            world.Frame();
            Assert.AreEqual(1, entered.Count);
            Assert.AreEqual(10, entered[0][0]);
            Assert.AreEqual(20, entered[0][1]);
            Assert.AreEqual(1, left.Count);
        }

        [Test]
        public void MVP15_ATextBox_ReportsTypingFocusAndFocusLostWithEnterPressed()
        {
            RbxGuiTestWorld world = World();
            RbxTextBox box = world.Create<RbxTextBox>("TextBox", world.Screen());
            box.TextState.SetText("old");
            List<object[]> focused = world.Listen(box.Focused);
            List<object[]> lost = world.Listen(box.FocusLost);
            int textChanges = 0;
            RbxScriptSignal textSignal = box.GetPropertyChangedSignal("Text");
            textSignal.BindScheduler(world.Scheduler);
            textSignal.Connect((System.Action<object[]>)(_ => textChanges++));
            world.Frame();
            FakeRbxUiBackend.FakeElement field = world.Element(box);
            Assert.AreEqual("TextField", field.Kind);

            box.CaptureFocus();
            Assert.IsTrue(box.IsFocused);
            Assert.AreEqual(string.Empty, field.Text, "ClearTextOnFocus empties the field");
            Assert.AreSame(box, world.Presenter.FocusedTextBox);
            field.ChangeValueByPlayer(RbxUiValue.FromText("hello"));
            Assert.AreEqual("hello", box.TextState.Text, "typing updates Text");
            field.SubmitByPlayer();
            world.Frame();

            Assert.IsFalse(box.IsFocused, "Enter releases focus, as in Roblox");
            Assert.AreEqual(1, focused.Count);
            Assert.AreEqual(1, lost.Count);
            Assert.AreEqual(true, lost[0][0], "enterPressed");
            Assert.GreaterOrEqual(textChanges, 2);

            box.CaptureFocus();
            box.ReleaseFocus(false);
            world.Frame();
            Assert.AreEqual(2, lost.Count);
            Assert.AreEqual(false, lost[1][0], "twin: a release without Enter");
        }

        [Test]
        public void MVP15_Image_ARegisteredKeyIsShown_AndARobloxAssetIdIsNot()
        {
            RbxGuiTestWorld world = World();
            RbxImageLabel image = world.Create<RbxImageLabel>("ImageLabel", world.Screen());
            image.ImageState.SetImage("rbxassetid://12345");
            world.Frame();
            FakeRbxUiBackend.FakeElement element = world.Element(image);
            Assert.AreEqual("Image", element.Kind);
            Assert.IsFalse(element.Attributes.ContainsKey("image"), "a Roblox asset id is never fetched");
            Assert.IsFalse(image.ImageState.IsDrawable);

            image.ImageState.SetImage("icons/coin");
            image.ImageState.SetImageTransparency(0.5d);
            world.Frame();
            Assert.AreEqual("icons/coin", element.Attributes["image"]);
            Assert.AreEqual("#FFFFFF80", element.Attributes["tint-color"]);
        }

        [Test]
        public void MVP15_UICornerAndUIStroke_DrawOnTheParent()
        {
            RbxGuiTestWorld world = World();
            RbxFrame frame = world.Create<RbxFrame>("Frame", world.Screen());
            frame.SetBorderSizePixel(0);
            RbxUiCorner corner = world.Create<RbxUiCorner>("UICorner", frame);
            corner.SetCornerRadius(new RbxUDim(0.5f, 0));
            RbxUiStroke stroke = world.Create<RbxUiStroke>("UIStroke", frame);
            stroke.SetThickness(3d);
            world.Frame();

            FakeRbxUiBackend.FakeElement element = world.Element(frame);
            Assert.AreEqual("53px", element.Typed(RbxUiStyleProperty.BorderTopLeftRadius).ToString(),
                "a 100px square at scale 0.5 is a circle (50px) plus the stroke outside it");
            Assert.AreEqual("3px", element.Typed(RbxUiStyleProperty.BorderTopWidth).ToString());

            stroke.SetEnabled(false);
            world.Frame();
            Assert.AreEqual("0px", element.Typed(RbxUiStyleProperty.BorderTopWidth).ToString(),
                "twin: a disabled stroke draws nothing");
        }

        [Test]
        public void MVP15_ScreenGuisAndUxmlScreens_StackOnOneSortOrderScale()
        {
            RbxGuiTestWorld world = World();
            RbxScreenGui gui = world.Screen();
            gui.SetDisplayOrder(5);
            world.Frame();
            RbxUiResult uxml = world.Backend.CreateScreen("m/hud", "m", "<ui:Label />", null, 3, true);

            Assert.Greater(world.HostScreen(gui).SortOrder, uxml.Screen.SortOrder,
                "DisplayOrder 5 draws over a UxmlService screen with SortOrder 3");
        }

        [Test]
        public void MVP15_TheLocalPlayerChange_MovesWhatIsShown()
        {
            RbxGuiTestWorld world = World();
            RbxScreenGui mine = world.Screen();
            world.Frame();
            Assert.AreEqual(1, world.Presenter.ScreenCount);

            world.LocalPlayer = null;
            world.Frame();
            Assert.AreEqual(0, world.Presenter.ScreenCount, "no local player, nothing on screen");
            Assert.IsTrue(world.Backend.Created[0].IsDestroyed);
            world.LocalPlayer = world.Player;
            world.Frame();
            Assert.IsNotNull(world.HostScreen(mine));
        }
    }
}
