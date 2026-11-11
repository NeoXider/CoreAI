using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.RuntimeUi;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Gui
{
    /// <summary>
    /// A ScrollingFrame scrolled by the player, engine-free over the fake host: the children are drawn on a canvas
    /// element sized to <c>AbsoluteCanvasSize</c>, a script's <c>CanvasPosition</c> moves the host's scroll offset
    /// and is charged, the player's scroll moves <c>CanvasPosition</c> without a charge and fires its signal,
    /// <c>ScrollingEnabled</c> and <c>ScrollingDirection</c> decide which axes the player may move, the offset stays
    /// inside the canvas, <c>AutomaticCanvasSize</c> fits a list on the first frame, and the bar members reach the
    /// bars. Every behaviour has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class RbxGuiScrollingEditModeTests
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

        /// <summary>A 200 by 100 frame at (50, 40) without a border, with a canvas 400 pixels tall.</summary>
        private RbxScrollingFrame Shop(RbxGuiTestWorld world, RbxScreenGui screen)
        {
            RbxScrollingFrame frame = world.Create<RbxScrollingFrame>("ScrollingFrame");
            frame.SetSize(new RbxUDim2(0f, 200, 0f, 100));
            frame.SetPosition(new RbxUDim2(0f, 50, 0f, 40));
            frame.SetBorderSizePixel(0);
            frame.SetCanvasSize(new RbxUDim2(0f, 0, 0f, 400));
            frame.Parent = screen;
            return frame;
        }

        private RbxFrame Row(RbxGuiTestWorld world, RbxInstance parent, int y)
        {
            RbxFrame row = world.Create<RbxFrame>("Frame");
            row.SetSize(new RbxUDim2(1f, 0, 0f, 30));
            row.SetPosition(new RbxUDim2(0f, 0, 0f, y));
            row.SetBorderSizePixel(0);
            row.Parent = parent;
            return row;
        }

        private static FakeRbxUiBackend.FakeElement Canvas(FakeRbxUiBackend.FakeElement scrollView)
        {
            Assert.AreEqual(1, scrollView.Children.Count, "a scroll view holds exactly its canvas");
            return scrollView.Children[0];
        }

        [Test]
        public void MVP15_Scroll_TheChildrenAreDrawnOnACanvasTheSizeOfAbsoluteCanvasSize()
        {
            RbxGuiTestWorld world = World();
            RbxScrollingFrame frame = Shop(world, world.Screen());
            RbxFrame row = Row(world, frame, 60);
            world.Frame();

            FakeRbxUiBackend.FakeElement view = world.Element(frame);
            FakeRbxUiBackend.FakeElement canvas = Canvas(view);
            Assert.AreEqual("ScrollView", view.Kind);
            Assert.AreEqual("VisualElement", canvas.Kind);
            Assert.IsTrue(canvas.Classes.Contains("rbx-canvas"));
            Assert.AreEqual("Ignore", canvas.Attributes["picking-mode"], "the canvas never takes the pointer");
            Assert.AreSame(canvas, world.Element(row).Parent, "the children are drawn on the canvas");
            Assert.AreEqual(new RbxVector2(200f, 400f), frame.AbsoluteCanvasSize);
            Assert.AreEqual(new RbxVector2(200f, 100f), frame.AbsoluteWindowSize);
            Assert.AreEqual("200px", canvas.Typed(RbxUiStyleProperty.Width).ToString());
            Assert.AreEqual("400px", canvas.Typed(RbxUiStyleProperty.Height).ToString(),
                "the canvas size is the scroll range the host lets the player move through");
            Assert.AreEqual(2, world.Presenter.ElementCount, "the frame and its row; the canvas is not a GUI element");
            Assert.AreEqual(2, world.World.CountElements(RbxGuiTestWorld.Mod), "and costs the mod no element budget");
        }

        [Test]
        public void MVP15_Scroll_AScriptsCanvasPosition_MovesTheHostOffsetAndTheChildren_AndIsCharged()
        {
            RbxGuiTestWorld world = World(new RbxUiBudgets { MaxWritesPerFrame = 1 });
            RbxScrollingFrame frame = Shop(world, world.Screen());
            RbxFrame row = Row(world, frame, 60);
            world.Frame();
            FakeRbxUiBackend.FakeElement view = world.Element(frame);
            string rowTop = world.Element(row).Typed(RbxUiStyleProperty.Top).ToString();

            frame.SetCanvasPosition(new RbxVector2(0f, 150f));
            RbxError refused = Assert.Throws<RbxError>(() => frame.SetCanvasPosition(new RbxVector2(0f, 151f)));
            StringAssert.Contains(RbxUiErrors.BudgetWrites, refused.Message, "a script's write is charged");
            world.Frame();

            Assert.AreEqual(150f, view.ScrollOffsetY, "the host scrolls to the script's offset");
            Assert.AreEqual(new RbxVector2(50f, 40f + 60f - 150f), row.AbsolutePosition,
                "AbsolutePosition is where the child is drawn after the scroll");
            Assert.AreEqual(rowTop, world.Element(row).Typed(RbxUiStyleProperty.Top).ToString(),
                "the child keeps its place on the canvas; the canvas moves");
            Assert.DoesNotThrow(() => frame.SetCanvasPosition(new RbxVector2(0f, 151f)), "the budget is per frame");
        }

        [Test]
        public void MVP15_Scroll_ThePlayersScroll_MovesCanvasPosition_FiresItsSignal_AndChargesNothing()
        {
            RbxGuiTestWorld world = World(new RbxUiBudgets { MaxWritesPerFrame = 1 });
            RbxScrollingFrame frame = Shop(world, world.Screen());
            RbxFrame row = Row(world, frame, 60);
            List<object[]> changed = world.Listen(frame.GetPropertyChangedSignal("CanvasPosition"));
            world.Frame();
            FakeRbxUiBackend.FakeElement view = world.Element(frame);
            int pushes = view.ScrollWrites;

            frame.SetScrollBarImageTransparency(0.5d);
            for (int i = 1; i <= 5; i++)
            {
                view.ScrollByPlayer(0f, 20f * i);
            }

            Assert.AreEqual(new RbxVector2(0f, 100f), frame.CanvasPosition, "CanvasPosition follows the player");
            Assert.AreEqual(pushes, view.ScrollWrites, "the player's offset is not pushed back to the host");
            RbxError refused = Assert.Throws<RbxError>(() => frame.SetCanvasPosition(new RbxVector2(0f, 7f)));
            StringAssert.Contains(RbxUiErrors.BudgetWrites, refused.Message,
                "five scrolls by the player used none of the one write; the script's own write already did");
            world.Frame();

            Assert.AreEqual(5, changed.Count, "the CanvasPosition signal fires for every scroll");
            Assert.AreEqual(new RbxVector2(50f, 40f + 60f - 100f), row.AbsolutePosition);
            Assert.AreEqual(pushes, view.ScrollWrites, "twin: the next frame does not echo the offset either");
        }

        [Test]
        public void MVP15_Scroll_ScrollingEnabledFalse_SnapsThePlayersScrollBack_ButAScriptStillMovesIt()
        {
            RbxGuiTestWorld world = World();
            RbxScrollingFrame frame = Shop(world, world.Screen());
            frame.SetScrollingEnabled(false);
            world.Frame();
            FakeRbxUiBackend.FakeElement view = world.Element(frame);

            view.ScrollByPlayer(0f, 80f);
            Assert.AreEqual(0f, view.ScrollOffsetY, "the host is scrolled straight back");
            Assert.AreEqual(new RbxVector2(0f, 0f), frame.CanvasPosition, "a frame with scrolling off does not move");
            Assert.AreEqual("Hidden", view.Attributes["vertical-scroller-visibility"], "no bar is drawn");
            Assert.AreEqual("Hidden", view.Attributes["horizontal-scroller-visibility"]);

            frame.SetCanvasPosition(new RbxVector2(0f, 60f));
            world.Frame();
            Assert.AreEqual(60f, view.ScrollOffsetY, "twin: a script's CanvasPosition still moves the canvas");

            frame.SetScrollingEnabled(true);
            world.Frame();
            view.ScrollByPlayer(0f, 90f);
            Assert.AreEqual(new RbxVector2(0f, 90f), frame.CanvasPosition, "twin: scrolling on, the player moves it");
            Assert.AreEqual("Auto", view.Attributes["vertical-scroller-visibility"]);
        }

        [Test]
        public void MVP15_Scroll_ScrollingDirectionY_KeepsXWhereTheScriptPutIt()
        {
            RbxGuiTestWorld world = World();
            RbxScrollingFrame frame = Shop(world, world.Screen());
            frame.SetCanvasSize(new RbxUDim2(0f, 600, 0f, 400));
            frame.SetScrollingDirection(RbxScrollingDirection.Y);
            frame.SetCanvasPosition(new RbxVector2(30f, 0f));
            world.Frame();
            FakeRbxUiBackend.FakeElement view = world.Element(frame);
            Assert.AreEqual("Vertical", view.Attributes["mode"]);
            Assert.AreEqual("Hidden", view.Attributes["horizontal-scroller-visibility"]);
            Assert.AreEqual(30f, view.ScrollOffsetX, "a script may still place X");

            view.ScrollByPlayer(200f, 70f);
            Assert.AreEqual(30f, view.ScrollOffsetX, "the player may not move X");
            Assert.AreEqual(new RbxVector2(30f, 70f), frame.CanvasPosition, "twin: Y follows the player");

            frame.SetScrollingDirection(RbxScrollingDirection.XY);
            world.Frame();
            view.ScrollByPlayer(200f, 70f);
            Assert.AreEqual(new RbxVector2(200f, 70f), frame.CanvasPosition, "XY lets the player move both");
            Assert.AreEqual("VerticalAndHorizontal", view.Attributes["mode"]);
        }

        [Test]
        public void MVP15_Scroll_ContentThatFitsTheWindow_DoesNotScroll()
        {
            RbxGuiTestWorld world = World();
            RbxScrollingFrame frame = Shop(world, world.Screen());
            frame.SetCanvasSize(new RbxUDim2(0f, 0, 0f, 0));
            world.Frame();
            FakeRbxUiBackend.FakeElement view = world.Element(frame);

            Assert.AreEqual(new RbxVector2(200f, 100f), frame.AbsoluteCanvasSize, "the canvas is never smaller than the window");
            Assert.AreEqual("100px", Canvas(view).Typed(RbxUiStyleProperty.Height).ToString(),
                "the host gets no range to scroll through");
            view.ScrollByPlayer(0f, 40f);
            Assert.AreEqual(0f, view.ScrollOffsetY);
            Assert.AreEqual(new RbxVector2(0f, 0f), frame.CanvasPosition);

            frame.SetCanvasPosition(new RbxVector2(0f, 40f));
            world.Frame();
            Assert.AreEqual(new RbxVector2(0f, 0f), frame.CanvasPosition, "a script's write is kept in range too");
            Assert.AreEqual(0f, view.ScrollOffsetY);
        }

        [Test]
        public void MVP15_Scroll_AWritePastTheEnd_StopsAtTheEnd()
        {
            RbxGuiTestWorld world = World();
            RbxScrollingFrame frame = Shop(world, world.Screen());
            world.Frame();
            FakeRbxUiBackend.FakeElement view = world.Element(frame);

            frame.SetCanvasPosition(new RbxVector2(-10f, 1000f));
            world.Frame();
            Assert.AreEqual(new RbxVector2(0f, 300f), frame.CanvasPosition, "canvas 400 minus window 100");
            Assert.AreEqual(300f, view.ScrollOffsetY);

            view.ScrollByPlayer(0f, 999f);
            Assert.AreEqual(new RbxVector2(0f, 300f), frame.CanvasPosition, "twin: the player stops at the end too");
        }

        [Test]
        public void MVP15_Scroll_AutomaticCanvasSize_FitsAListOnTheFirstFrame()
        {
            RbxGuiTestWorld world = World();
            RbxScreenGui screen = world.Screen();
            RbxScrollingFrame fitted = Shop(world, screen);
            RbxScrollingFrame fixedCanvas = Shop(world, screen);
            RbxScrollingFrame[] frames = { fitted, fixedCanvas };
            for (int f = 0; f < frames.Length; f++)
            {
                frames[f].SetCanvasSize(new RbxUDim2(0f, 0, 0f, 0));
                world.Create<RbxUiListLayout>("UIListLayout", frames[f]);
                RbxUiPadding padding = world.Create<RbxUiPadding>("UIPadding", frames[f]);
                padding.SetPaddingTop(new RbxUDim(0f, 10));
                padding.SetPaddingBottom(new RbxUDim(0f, 10));
                for (int i = 0; i < 20; i++)
                {
                    Row(world, frames[f], 0);
                }
            }

            fitted.SetAutomaticCanvasSize(RbxAutomaticSize.Y);
            world.Frame();

            Assert.AreEqual(new RbxVector2(200f, 620f), fitted.AbsoluteCanvasSize,
                "20 rows of 30 plus the padding, already on the first frame");
            Assert.AreEqual(new RbxVector2(200f, 100f), fixedCanvas.AbsoluteCanvasSize,
                "twin: without AutomaticCanvasSize the canvas stays CanvasSize (here the window)");
            Assert.AreEqual(new RbxVector2(50f, 40f + 10f + 19 * 30f), ((RbxFrame)fitted.GetChildren()[21]).AbsolutePosition,
                "the last row sits at the bottom of the fitted canvas");
            fitted.SetCanvasPosition(new RbxVector2(0f, 520f));
            world.Frame();
            Assert.AreEqual(new RbxVector2(0f, 520f), fitted.CanvasPosition, "the fitted canvas scrolls to its end");
        }

        [Test]
        public void MVP15_Scroll_TheBarMembers_ReachTheBars_AndThicknessZeroDrawsNone()
        {
            RbxGuiTestWorld world = World();
            RbxScrollingFrame frame = Shop(world, world.Screen());
            frame.SetScrollBarThickness(6);
            frame.SetScrollBarImageColor3(new RbxColor3(1f, 0f, 0f));
            frame.SetScrollBarImageTransparency(0.25d);
            world.Frame();
            FakeRbxUiBackend.FakeElement view = world.Element(frame);

            Assert.AreEqual("6px", view.Styles["--rbx-scroll-bar-size"]);
            Assert.AreEqual(RbxUiStyleValue.Color(1f, 0f, 0f, 0.75f).ToString(), view.Styles["--rbx-scroll-bar-color"]);
            Assert.IsTrue(view.Classes.Contains("rbx-scroll-right"), "the vertical bar is on the right by default");
            Assert.AreEqual("Auto", view.Attributes["vertical-scroller-visibility"]);

            frame.SetScrollBarThickness(0);
            frame.SetVerticalScrollBarPosition(1);
            world.Frame();
            Assert.AreEqual("Hidden", view.Attributes["vertical-scroller-visibility"], "thickness 0 draws no bar");
            Assert.AreEqual("Hidden", view.Attributes["horizontal-scroller-visibility"]);
            Assert.IsTrue(view.Classes.Contains("rbx-scroll-left"));
            Assert.IsFalse(view.Classes.Contains("rbx-scroll-right"), "the side moves, it does not double");
        }

        [Test]
        public void MVP15_Scroll_VerticalScrollBarInset_TakesTheBarsStripFromTheWindow()
        {
            RbxGuiTestWorld world = World();
            RbxScreenGui screen = world.Screen();
            RbxScrollingFrame over = Shop(world, screen);
            RbxScrollingFrame inset = Shop(world, screen);
            RbxScrollingFrame idle = Shop(world, screen);
            RbxScrollingFrame always = Shop(world, screen);
            inset.SetVerticalScrollBarInset(1);
            idle.SetVerticalScrollBarInset(1);
            idle.SetCanvasSize(new RbxUDim2(0f, 0, 0f, 0));
            always.SetVerticalScrollBarInset(2);
            always.SetCanvasSize(new RbxUDim2(0f, 0, 0f, 0));
            RbxFrame overRow = Row(world, over, 0);
            RbxFrame insetRow = Row(world, inset, 0);
            world.Frame();

            Assert.AreEqual(new RbxVector2(200f, 100f), over.AbsoluteWindowSize, "Inset None: the bar lies over the canvas");
            Assert.AreEqual(200f, overRow.AbsoluteSize.X);
            Assert.AreEqual(new RbxVector2(188f, 100f), inset.AbsoluteWindowSize, "Inset ScrollBar while the bar shows");
            Assert.AreEqual(188f, insetRow.AbsoluteSize.X, "a full-width child stops at the bar");
            Assert.AreEqual("200px", Canvas(world.Element(inset)).Typed(RbxUiStyleProperty.Width).ToString(),
                "the host canvas keeps the bar's strip");
            Assert.AreEqual(new RbxVector2(200f, 100f), idle.AbsoluteWindowSize, "twin: no bar shows, nothing is taken");
            Assert.AreEqual(new RbxVector2(188f, 100f), always.AbsoluteWindowSize, "Inset Always, even without a bar");
        }

        [Test]
        public void MVP15_Scroll_ALeftBarWithInset_PutsTheChildrenRightOfIt()
        {
            RbxGuiTestWorld world = World();
            RbxScrollingFrame frame = Shop(world, world.Screen());
            frame.SetVerticalScrollBarPosition(1);
            frame.SetVerticalScrollBarInset(1);
            RbxFrame row = Row(world, frame, 0);
            world.Frame();

            Assert.AreEqual(new RbxVector2(50f + 12f, 40f), row.AbsolutePosition, "the 12-pixel bar is on the left");
            Assert.AreEqual(188f, row.AbsoluteSize.X);
            frame.SetVerticalScrollBarPosition(0);
            world.Frame();
            Assert.AreEqual(new RbxVector2(50f, 40f), row.AbsolutePosition, "twin: on the right, nothing moves");
        }

        [Test]
        public void MVP15_Scroll_AnIdleFrame_PushesNothing_AndRemovalLeavesNoSubscriber()
        {
            RbxGuiTestWorld world = World();
            RbxScrollingFrame frame = Shop(world, world.Screen());
            Row(world, frame, 60);
            world.Frame();
            FakeRbxUiBackend.FakeElement view = world.Element(frame);
            FakeRbxUiBackend.FakeElement canvas = Canvas(view);
            int scrolls = view.ScrollWrites;
            int styles = canvas.TypedStyleWrites;
            int attributes = view.Attributes.Count;

            world.Frame();
            frame.SetCanvasPosition(frame.CanvasPosition);
            world.Frame();
            Assert.AreEqual(scrolls, view.ScrollWrites, "nothing changed, no offset is pushed");
            Assert.AreEqual(styles, canvas.TypedStyleWrites, "nor a canvas size");
            Assert.AreEqual(attributes, view.Attributes.Count);
            Assert.AreEqual(1, view.SubscriberCount - CountPointer(view), "the presenter listens to the scroll");

            frame.Parent = null;
            world.Frame();
            Assert.IsFalse(view.IsAlive);
            Assert.IsFalse(canvas.IsAlive, "the canvas goes with its frame");
            Assert.AreEqual(0, view.SubscriberCount);
            Assert.AreEqual(0, world.World.CountElements(RbxGuiTestWorld.Mod));
        }

        [Test]
        public void MVP15_Scroll_TheScreenUss_WithItsScrollBarRules_ParsesWithoutAReport()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(RbxGuiPresenter.ScreenUss);
            Assert.AreEqual(0, sheet.Diagnostics.Count, sheet.Diagnostics.Count > 0 ? sheet.Diagnostics[0].ToString() : "");

            UssStyleSheet broken = UssStyleSheet.Parse(RbxGuiPresenter.ScreenUss + ".rbx-canvas { gap: 4px; }");
            Assert.Greater(broken.Diagnostics.Count, 0, "twin: an unsupported property is reported, so the check can fail");
        }

        [Test]
        public void MVP15_Scroll_TheScreenUss_SizesAndPlacesTheBars_FromTheFramesCustomProperties()
        {
            StyleNode view = new("ScrollView", null, "rbx-gui", "rbx-scroll", "rbx-scroll-right");
            StyleNode container = view.Add(new StyleNode("VisualElement", null,
                "unity-scroll-view__content-and-vertical-scroll-container"));
            StyleNode viewport = container.Add(new StyleNode("VisualElement", null, "unity-scroll-view__content-viewport"));
            StyleNode canvas = viewport.Add(new StyleNode("VisualElement", null, "unity-scroll-view__content-container"))
                .Add(new StyleNode("VisualElement", null, "rbx-canvas"));
            StyleNode vertical = container.Add(new StyleNode("Scroller", null, "unity-scroller", "unity-scroller--vertical"));
            StyleNode lowButton = vertical.Add(new StyleNode("RepeatButton", null, "unity-scroller__low-button"));
            StyleNode dragger = vertical.Add(new StyleNode("Slider", null, "unity-scroller__slider"))
                .Add(new StyleNode("VisualElement", null, "unity-base-slider__dragger"));
            StyleNode horizontal = view.Add(new StyleNode("Scroller", null, "unity-scroller", "unity-scroller--horizontal"));
            StyleNode nested = canvas.Add(new StyleNode("TextField", null, "rbx-gui", "rbx-textbox"))
                .Add(new StyleNode("Scroller", null, "unity-scroller", "unity-scroller--vertical"));
            RuntimeUiDiagnosticList reports = new();
            List<UssDeclaration> overrides = new()
            {
                UssDeclaration.Create("--rbx-scroll-bar-size", "8px", 0, 0, RuntimeUiSource.Api, reports),
                UssDeclaration.Create("--rbx-scroll-bar-color", "rgb(255, 0, 0)", 0, 0, RuntimeUiSource.Api, reports)
            };
            StyleCascade cascade = new(UssStyleSheet.Parse(RbxGuiPresenter.ScreenUss), view, overrides);

            UssResolvedStyle bar = cascade.Resolve(vertical);
            Assert.AreEqual("absolute", bar.Get(UssPropertyId.Position).ToUssText(), "the bar lies over the canvas");
            Assert.AreEqual("8px", bar.Get(UssPropertyId.Width).ToUssText(), "ScrollBarThickness is the bar's width");
            Assert.AreEqual("0px", bar.Get(UssPropertyId.Right).ToUssText());
            Assert.IsFalse(bar.IsSet(UssPropertyId.Left), "on the right side only");
            Assert.AreEqual("8px", cascade.Resolve(horizontal).Get(UssPropertyId.Height).ToUssText());
            Assert.AreEqual("none", cascade.Resolve(lowButton).Get(UssPropertyId.Display).ToUssText(), "no arrows");
            Assert.AreEqual("rgba(255, 0, 0, 1)", cascade.Resolve(dragger).Get(UssPropertyId.BackgroundColor).ToUssText(),
                "ScrollBarImageColor3 colors the bar");
            Assert.AreEqual("hidden", cascade.Resolve(canvas).Get(UssPropertyId.Overflow).ToUssText());
            Assert.IsFalse(cascade.Resolve(nested).IsSet(UssPropertyId.Width),
                "twin: a scroller inside a child field is not a bar of the frame");
            Assert.AreEqual(0, reports.Items.Count + cascade.Reports.Items.Count);

            view.Classes.Remove("rbx-scroll-right");
            view.Classes.Add("rbx-scroll-left");
            bar = cascade.Resolve(vertical);
            Assert.AreEqual("0px", bar.Get(UssPropertyId.Left).ToUssText(), "VerticalScrollBarPosition Left");
            Assert.IsFalse(bar.IsSet(UssPropertyId.Right));
        }

        /// <summary>An engine-free styled element: a type, classes and a parent.</summary>
        private sealed class StyleNode : IUssElement
        {
            public StyleNode(string typeName, string name, params string[] classes)
            {
                TypeName = typeName;
                Name = name;
                Classes = new HashSet<string>(classes);
            }

            public string TypeName { get; }

            public string Name { get; }

            public HashSet<string> Classes { get; }

            public StyleNode Parent { get; private set; }

            public IUssElement StyleParent => Parent;

            public UssPseudoState PseudoStates => UssPseudoState.None;

            public bool HasClass(string className)
            {
                return Classes.Contains(className);
            }

            public StyleNode Add(StyleNode child)
            {
                child.Parent = this;
                return child;
            }
        }

        /// <summary>Resolves an element after its ancestors, so custom properties inherit as in the host.</summary>
        private sealed class StyleCascade
        {
            private readonly List<UssStyleSheet> _sheets;
            private readonly StyleNode _owner;
            private readonly IReadOnlyList<UssDeclaration> _overrides;
            private readonly UssStyleResolver _resolver;

            public StyleCascade(UssStyleSheet sheet, StyleNode owner, IReadOnlyList<UssDeclaration> overrides)
            {
                _sheets = new List<UssStyleSheet> { sheet };
                _owner = owner;
                _overrides = overrides;
                Reports = new RuntimeUiDiagnosticList();
                _resolver = new UssStyleResolver(Reports);
            }

            public RuntimeUiDiagnosticList Reports { get; }

            public UssResolvedStyle Resolve(StyleNode node)
            {
                UssCustomProperties inherited = node.Parent == null ? UssCustomProperties.Empty
                    : Resolve(node.Parent).CustomProperties;
                UssResolvedStyle output = new();
                _resolver.Resolve(_sheets, 0, node, inherited, null, ReferenceEquals(node, _owner) ? _overrides : null,
                    output);
                return output;
            }
        }

        private static int CountPointer(FakeRbxUiBackend.FakeElement element)
        {
            // WHY two: every shown GUI object listens to PointerEntered and PointerExited for MouseEnter and MouseLeave.
            return element.IsAlive ? 2 : 0;
        }
    }
}
