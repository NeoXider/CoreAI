using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Gui
{
    /// <summary>
    /// The Roblox GUI layout rules, engine-free: scale-plus-offset sizing and positioning against the parent,
    /// AnchorPoint, SizeConstraint, UIPadding, UIListLayout, UIGridLayout, the size and aspect constraints,
    /// AutomaticSize and rotation, read back through AbsolutePosition and AbsoluteSize against a 1280 by 720
    /// fake viewport. Each rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class RbxGuiLayoutEditModeTests
    {
        private RbxGuiTestWorld _world;

        [SetUp]
        public void SetUp()
        {
            _world = new RbxGuiTestWorld();
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        private RbxFrame Frame(RbxInstance parent, RbxUDim2 size, RbxUDim2 position)
        {
            RbxFrame frame = _world.Create<RbxFrame>("Frame");
            frame.SetSize(size);
            frame.SetPosition(position);
            frame.Parent = parent;
            return frame;
        }

        [Test]
        public void MVP15_Layout_SizeAndPositionAreScaleOfTheParentPlusOffset_AndAnchorPointShiftsTheBox()
        {
            RbxScreenGui screen = _world.Screen();
            RbxFrame frame = Frame(screen, new RbxUDim2(0.5f, 10, 0.25f, 0), new RbxUDim2(0.5f, 0, 0.5f, 0));
            frame.SetAnchorPoint(new RbxVector2(0.5f, 0.5f));

            Assert.AreEqual(new RbxVector2(1280f, 720f), screen.AbsoluteSize, "a ScreenGui is the viewport");
            Assert.AreEqual(new RbxVector2(650f, 180f), frame.AbsoluteSize);
            Assert.AreEqual(new RbxVector2(640f - 325f, 360f - 90f), frame.AbsolutePosition,
                "AnchorPoint (0.5, 0.5) centres the box on its Position");
        }

        [Test]
        public void MVP15_Layout_AChildIsPlacedInItsParentsBox()
        {
            RbxScreenGui screen = _world.Screen();
            RbxFrame parent = Frame(screen, new RbxUDim2(0f, 400, 0f, 200), new RbxUDim2(0f, 100, 0f, 50));
            RbxFrame child = Frame(parent, new RbxUDim2(0.5f, 0, 1f, -20), new RbxUDim2(1f, -10, 0f, 10));
            child.SetAnchorPoint(new RbxVector2(1f, 0f));

            Assert.AreEqual(new RbxVector2(200f, 180f), child.AbsoluteSize);
            Assert.AreEqual(new RbxVector2(100f + 400f - 10f - 200f, 50f + 10f), child.AbsolutePosition);
        }

        [Test]
        public void MVP15_Layout_SizeConstraintRelativeXX_ReadsBothScalesFromTheParentWidth()
        {
            RbxScreenGui screen = _world.Screen();
            RbxFrame square = Frame(screen, new RbxUDim2(0.1f, 0, 0.1f, 0), new RbxUDim2());
            square.SetSizeConstraint(RbxSizeConstraint.RelativeXX);

            Assert.AreEqual(new RbxVector2(128f, 128f), square.AbsoluteSize);
            square.SetSizeConstraint(RbxSizeConstraint.RelativeXY);
            Assert.AreEqual(new RbxVector2(128f, 72f), square.AbsoluteSize, "twin: RelativeXY reads each axis");
        }

        [Test]
        public void MVP15_Layout_UIPadding_InsetsTheChildrensArea_ButNotTheObjectItself()
        {
            RbxScreenGui screen = _world.Screen();
            RbxFrame parent = Frame(screen, new RbxUDim2(0f, 200, 0f, 100), new RbxUDim2(0f, 0, 0f, 0));
            RbxUiPadding padding = _world.Create<RbxUiPadding>("UIPadding", parent);
            padding.SetPaddingLeft(new RbxUDim(0f, 10));
            padding.SetPaddingTop(new RbxUDim(0.1f, 0));
            padding.SetPaddingRight(new RbxUDim(0f, 10));
            RbxFrame child = Frame(parent, new RbxUDim2(1f, 0, 1f, 0), new RbxUDim2());

            Assert.AreEqual(new RbxVector2(200f, 100f), parent.AbsoluteSize);
            Assert.AreEqual(new RbxVector2(10f, 10f), child.AbsolutePosition);
            Assert.AreEqual(new RbxVector2(180f, 90f), child.AbsoluteSize);

            padding.Destroy();
            Assert.AreEqual(new RbxVector2(200f, 100f), child.AbsoluteSize, "twin: without padding it fills the box");
        }

        [Test]
        public void MVP15_Layout_UIListLayout_StacksVisibleSiblingsByLayoutOrder_WithPadding()
        {
            RbxScreenGui screen = _world.Screen();
            RbxFrame list = Frame(screen, new RbxUDim2(0f, 300, 0f, 400), new RbxUDim2(0f, 20, 0f, 30));
            RbxUiListLayout layout = _world.Create<RbxUiListLayout>("UIListLayout", list);
            layout.SetPadding(new RbxUDim(0f, 5));
            RbxFrame second = Frame(list, new RbxUDim2(0f, 100, 0f, 40), new RbxUDim2(0f, 999, 0f, 999));
            second.SetLayoutOrder(2);
            RbxFrame hidden = Frame(list, new RbxUDim2(0f, 100, 0f, 40), new RbxUDim2());
            hidden.SetLayoutOrder(1);
            hidden.SetVisible(false);
            RbxFrame first = Frame(list, new RbxUDim2(1f, 0, 0f, 20), new RbxUDim2());
            first.SetLayoutOrder(0);

            Assert.AreEqual(new RbxVector2(20f, 30f), first.AbsolutePosition, "LayoutOrder 0 comes first");
            Assert.AreEqual(new RbxVector2(300f, 20f), first.AbsoluteSize);
            Assert.AreEqual(new RbxVector2(20f, 30f + 20f + 5f), second.AbsolutePosition,
                "Position is ignored and the invisible sibling takes no room");
            Assert.AreEqual(new RbxVector2(300f, 65f), layout.AbsoluteContentSize);

            layout.SetHorizontalAlignment(RbxHorizontalAlignment.Center);
            Assert.AreEqual(20f + 100f, second.AbsolutePosition.X, "centred across the column");
            layout.SetHorizontalAlignment(RbxHorizontalAlignment.Left);
            layout.SetFillDirection(RbxFillDirection.Horizontal);
            Assert.AreEqual(new RbxVector2(20f + 300f + 5f, 30f), second.AbsolutePosition, "a row");
        }

        [Test]
        public void MVP15_Layout_UIGridLayout_FillsRowsWithCellsOfCellSize()
        {
            RbxScreenGui screen = _world.Screen();
            RbxFrame grid = Frame(screen, new RbxUDim2(0f, 230, 0f, 300), new RbxUDim2());
            RbxUiGridLayout layout = _world.Create<RbxUiGridLayout>("UIGridLayout", grid);
            RbxFrame[] cells = new RbxFrame[3];
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = Frame(grid, new RbxUDim2(0f, 1, 0f, 1), new RbxUDim2());
            }

            Assert.AreEqual(new RbxVector2(100f, 100f), cells[0].AbsoluteSize, "CellSize overrides Size");
            Assert.AreEqual(new RbxVector2(0f, 0f), cells[0].AbsolutePosition);
            Assert.AreEqual(new RbxVector2(105f, 0f), cells[1].AbsolutePosition, "CellPadding 5 between cells");
            Assert.AreEqual(new RbxVector2(0f, 105f), cells[2].AbsolutePosition, "two cells fit a 230px row");
            Assert.AreEqual(new RbxVector2(2f, 2f), layout.AbsoluteCellCount);

            layout.SetFillDirectionMaxCells(1);
            Assert.AreEqual(new RbxVector2(0f, 105f), cells[1].AbsolutePosition, "twin: one cell per row");
        }

        [Test]
        public void MVP15_Layout_Constraints_AspectRatioFitsInsideTheSize_AndSizeConstraintClamps()
        {
            RbxScreenGui screen = _world.Screen();
            RbxFrame box = Frame(screen, new RbxUDim2(0f, 400, 0f, 100), new RbxUDim2());
            RbxUiAspectRatioConstraint aspect = _world.Create<RbxUiAspectRatioConstraint>("UIAspectRatioConstraint", box);
            aspect.SetAspectRatio(2d);
            Assert.AreEqual(new RbxVector2(200f, 100f), box.AbsoluteSize);
            aspect.Destroy();

            RbxUiSizeConstraint clamp = _world.Create<RbxUiSizeConstraint>("UISizeConstraint", box);
            clamp.SetMaxSize(new RbxVector2(300f, 300f));
            clamp.SetMinSize(new RbxVector2(0f, 150f));
            Assert.AreEqual(new RbxVector2(300f, 150f), box.AbsoluteSize);
            clamp.Destroy();
            Assert.AreEqual(new RbxVector2(400f, 100f), box.AbsoluteSize, "twin: unconstrained");
            Assert.Throws<RbxError>(() => _world.Create<RbxUiAspectRatioConstraint>("UIAspectRatioConstraint")
                .SetAspectRatio(0d), "AspectRatio must be above 0");
        }

        [Test]
        public void MVP15_Layout_AutomaticSize_GrowsTheBoxToItsList()
        {
            RbxScreenGui screen = _world.Screen();
            RbxFrame panel = Frame(screen, new RbxUDim2(0f, 100, 0f, 0), new RbxUDim2());
            panel.SetAutomaticSize(RbxAutomaticSize.Y);
            _world.Create<RbxUiListLayout>("UIListLayout", panel);
            Frame(panel, new RbxUDim2(0f, 100, 0f, 30), new RbxUDim2());
            Frame(panel, new RbxUDim2(0f, 100, 0f, 30), new RbxUDim2());

            Assert.AreEqual(new RbxVector2(100f, 60f), panel.AbsoluteSize);
            panel.SetAutomaticSize(RbxAutomaticSize.None);
            Assert.AreEqual(new RbxVector2(100f, 0f), panel.AbsoluteSize, "twin: without AutomaticSize it stays 0 high");
        }

        [Test]
        public void MVP15_Layout_RotationAddsUp_AndNeverMovesTheBox()
        {
            RbxScreenGui screen = _world.Screen();
            RbxFrame parent = Frame(screen, new RbxUDim2(0f, 100, 0f, 100), new RbxUDim2(0f, 50, 0f, 50));
            parent.SetRotation(30d);
            RbxFrame child = Frame(parent, new RbxUDim2(0f, 10, 0f, 10), new RbxUDim2(0f, 5, 0f, 5));
            child.SetRotation(15d);

            Assert.AreEqual(45f, child.AbsoluteRotation);
            Assert.AreEqual(new RbxVector2(55f, 55f), child.AbsolutePosition, "AbsolutePosition ignores rotation");
        }

        [Test]
        public void MVP15_Layout_AResizedViewport_LaysOutAgain()
        {
            RbxScreenGui screen = _world.Screen();
            RbxFrame half = Frame(screen, new RbxUDim2(0.5f, 0, 0.5f, 0), new RbxUDim2());
            Assert.AreEqual(new RbxVector2(640f, 360f), half.AbsoluteSize);

            _world.HostScreen(screen).Resize(800f, 600f);

            Assert.AreEqual(new RbxVector2(400f, 300f), half.AbsoluteSize);
        }

        [Test]
        public void MVP15_Layout_NotOnScreen_AbsoluteValuesStayZero_NegativeTwin()
        {
            RbxScreenGui starter = _world.Create<RbxScreenGui>("ScreenGui", _world.Game.GetService("StarterGui"));
            RbxFrame inStarter = Frame(starter, new RbxUDim2(0f, 100, 0f, 100), new RbxUDim2());
            RbxFrame loose = Frame(null, new RbxUDim2(0f, 100, 0f, 100), new RbxUDim2());
            RbxScreenGui shown = _world.Screen();
            RbxFrame inFolder = Frame(null, new RbxUDim2(0f, 100, 0f, 100), new RbxUDim2());
            RbxInstance holder = _world.Create<RbxInstance>("Folder", shown);
            inFolder.Parent = holder;

            Assert.AreEqual(RbxVector2.Zero, inStarter.AbsoluteSize, "StarterGui is not drawn");
            Assert.AreEqual(RbxVector2.Zero, loose.AbsoluteSize);
            Assert.AreEqual(RbxVector2.Zero, inFolder.AbsoluteSize,
                "a GuiObject under a Folder is not drawn, as in Roblox");
        }
    }
}
