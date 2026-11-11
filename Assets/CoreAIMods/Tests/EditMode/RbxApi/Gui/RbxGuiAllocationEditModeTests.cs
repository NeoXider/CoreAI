using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.Tests.EditMode.Performance;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Gui
{
    /// <summary>
    /// What the Roblox GUI entrance costs per frame, engine-free over the fake host (whose writes allocate
    /// nothing), with the allocation-budget pattern of <c>PartWriteAllocationBudgetEditModeTests</c>: an idle
    /// frame of a 200-object screen allocates nothing, and a property write on a shown label, including the
    /// push it causes, allocates nothing either (text, colour, transparency, and a position write that lays
    /// the screen out again). The negative twins add one allocation per operation and must break the budget.
    /// </summary>
    [TestFixture]
    public sealed class RbxGuiAllocationEditModeTests
    {
        private const int Operations = 2000;

        private RbxGuiTestWorld _world;
        private RbxTextLabel _label;

        [SetUp]
        public void SetUp()
        {
            _world = new RbxGuiTestWorld(new RbxUiBudgets
            {
                MaxWritesPerFrame = int.MaxValue,
                MaxElementsPerMod = 1000
            });
            RbxScreenGui screen = _world.Screen();
            RbxFrame panel = _world.Create<RbxFrame>("Frame", screen);
            _world.Create<RbxUiListLayout>("UIListLayout", panel);
            for (int i = 0; i < 198; i++)
            {
                RbxTextLabel row = _world.Create<RbxTextLabel>("TextLabel", panel);
                row.TextState.SetText("row " + i);
            }

            _label = _world.Create<RbxTextLabel>("TextLabel", screen);
            _world.Presenter.Flush();
            Assert.AreEqual(200, _world.Presenter.ElementCount);
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        [Test]
        public void MVP15_AnIdleFrame_AllocatesNothing()
        {
            AllocationMeter.AssertWithinBudget("idle GUI frame", count =>
            {
                for (int i = 0; i < count; i++)
                {
                    _world.Presenter.Flush();
                    _world.World.EndFrame();
                }
            }, Operations, 0);
        }

        [Test]
        public void MVP15_TextWriteOnAShownLabel_AllocatesNothing()
        {
            string[] texts = { "Score: 1", "Score: 2" };
            AllocationMeter.AssertWithinBudget("TextLabel.Text write + push", count =>
            {
                for (int i = 0; i < count; i++)
                {
                    _label.TextState.SetText(texts[i & 1]);
                    _world.Presenter.Flush();
                }
            }, Operations, 0);
        }

        [Test]
        public void MVP15_ColorAndTransparencyWrites_AllocateNothing()
        {
            RbxColor3[] colors = { new(1f, 0f, 0f), new(0f, 1f, 0f) };
            AllocationMeter.AssertWithinBudget("TextColor3 and BackgroundTransparency write + push", count =>
            {
                for (int i = 0; i < count; i++)
                {
                    _label.TextState.SetTextColor3(colors[i & 1]);
                    _label.SetBackgroundTransparency((i & 1) * 0.5d);
                    _world.Presenter.Flush();
                }
            }, Operations, 0);
        }

        [Test]
        public void MVP15_PositionWrite_LaysTheScreenOutAgain_WithoutAllocating()
        {
            RbxUDim2[] positions = { new(0f, 10, 0f, 10), new(0.5f, 0, 0.5f, 0) };
            AllocationMeter.AssertWithinBudget("GuiObject.Position write + relayout + push", count =>
            {
                for (int i = 0; i < count; i++)
                {
                    _label.SetPosition(positions[i & 1]);
                    _world.Presenter.Flush();
                }
            }, Operations / 10, 0);
        }

        [Test]
        public void MVP15_CanvasPositionWritesAndPlayerScrolls_AllocateNothing()
        {
            RbxScrollingFrame list = _world.Create<RbxScrollingFrame>("ScrollingFrame");
            list.SetSize(new RbxUDim2(0f, 200, 0f, 100));
            list.SetCanvasSize(new RbxUDim2(0f, 0, 0f, 1000));
            list.Parent = _world.Screen();
            _world.Create<RbxUiListLayout>("UIListLayout", list);
            for (int i = 0; i < 20; i++)
            {
                _world.Create<RbxTextLabel>("TextLabel", list).TextState.SetText("item " + i);
            }

            _world.Presenter.Flush();
            CoreAI.Tests.EditMode.RbxApi.RuntimeUi.FakeRbxUiBackend.FakeElement view = _world.Element(list);
            RbxVector2[] positions = { new(0f, 100f), new(0f, 300f) };
            AllocationMeter.AssertWithinBudget("CanvasPosition write + relayout + push, then a player scroll", count =>
            {
                for (int i = 0; i < count; i++)
                {
                    list.SetCanvasPosition(positions[i & 1]);
                    _world.Presenter.Flush();
                    view.ScrollByPlayer(0f, 50f + (i & 1));
                    _world.Presenter.Flush();
                }
            }, Operations / 10, 0);
        }

        [Test]
        public void MVP15_TheBudget_CatchesAnAllocatingWrite_NegativeTwin()
        {
            string[] texts = { "Score: 1", "Score: 2" };
            AllocationMeter.AssertTwinBreaksBudget("TextLabel.Text write + push", count =>
            {
                for (int i = 0; i < count; i++)
                {
                    _label.TextState.SetText(texts[i & 1]);
                    _world.Presenter.Flush();
                }
            }, Operations, 0);
        }
    }
}
