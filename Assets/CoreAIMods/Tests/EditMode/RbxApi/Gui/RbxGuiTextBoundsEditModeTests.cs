using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Gui
{
    /// <summary>
    /// Text measuring in the GUI layout (Roblox's <c>TextBounds</c> and <c>AutomaticSize</c> of a text object): the
    /// bounds are the text's size, wrapped at the box when <c>TextWrapped</c>; an automatically sized label grows to
    /// its text and a list stacks the grown labels; the host's measurement wins over the estimate and a new one is
    /// taken once more on the next frame; a label nobody measures is a repaint only. The estimate itself is pinned
    /// too.
    /// </summary>
    [TestFixture]
    public sealed class RbxGuiTextBoundsEditModeTests
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

        private RbxTextLabel Label(RbxInstance parent, string text, RbxUDim2 size, bool wrapped = false,
            RbxAutomaticSize automatic = RbxAutomaticSize.None)
        {
            RbxTextLabel label = _world.Create<RbxTextLabel>("TextLabel");
            label.SetSize(size);
            label.TextState.SetText(text);
            label.TextState.SetTextSize(10d);
            label.TextState.SetTextWrapped(wrapped);
            label.SetAutomaticSize(automatic);
            label.Parent = parent;
            return label;
        }

        [Test]
        public void TextBounds_IsTheTextsSize_WrappedAtTheBoxWhenTextWrapped()
        {
            RbxScreenGui screen = _world.Screen();
            RbxTextLabel line = Label(screen, "hello world", new RbxUDim2(0f, 30, 0f, 20));
            RbxTextLabel wrapped = Label(screen, "hello world", new RbxUDim2(0f, 30, 0f, 20), true);

            Assert.AreEqual(new RbxVector2(55f, 10f), line.TextState.TextBounds, "one line, five pixels a character");
            Assert.AreEqual(new RbxVector2(25f, 20f), wrapped.TextState.TextBounds, "broken at the space");
        }

        [Test]
        public void AutomaticSizeY_GrowsAWrappedLabelToItsText_AndAListStacksTheGrownLabels()
        {
            RbxScreenGui screen = _world.Screen();
            RbxFrame column = _world.Create<RbxFrame>("Frame");
            column.SetSize(new RbxUDim2(0f, 50, 0f, 400));
            column.Parent = screen;
            _world.Create<RbxUiListLayout>("UIListLayout", column);
            RbxTextLabel first = Label(column, "one two three four", new RbxUDim2(1f, 0, 0f, 0), true,
                RbxAutomaticSize.Y);
            RbxTextLabel second = Label(column, "five", new RbxUDim2(1f, 0, 0f, 0), true, RbxAutomaticSize.Y);

            Assert.AreEqual(new RbxVector2(50f, 20f), first.AbsoluteSize, "two lines of ten characters at most");
            Assert.AreEqual(new RbxVector2(50f, 10f), second.AbsoluteSize);
            Assert.AreEqual(first.AbsolutePosition.Y + 20f, second.AbsolutePosition.Y, "the list stacks the grown label");

            first.TextState.SetText("one");
            _world.Presenter.Flush();
            Assert.AreEqual(10f, first.AbsoluteSize.Y, "a text change lays the screen out again");
            Assert.AreEqual(first.AbsolutePosition.Y + 10f, second.AbsolutePosition.Y);
        }

        [Test]
        public void AutomaticSizeX_GrowsToTheLine_AndPaddingIsAdded()
        {
            RbxScreenGui screen = _world.Screen();
            RbxTextLabel label = Label(screen, "abcd", new RbxUDim2(0f, 5, 0f, 5), false, RbxAutomaticSize.XY);
            RbxUiPadding padding = _world.Create<RbxUiPadding>("UIPadding", label);
            padding.SetPaddingLeft(new RbxUDim(0f, 3));
            padding.SetPaddingRight(new RbxUDim(0f, 3));
            padding.SetPaddingTop(new RbxUDim(0f, 2));
            padding.SetPaddingBottom(new RbxUDim(0f, 2));

            Assert.AreEqual(new RbxVector2(26f, 14f), label.AbsoluteSize);
        }

        [Test]
        public void Negative_WithoutAutomaticSize_TheBoxKeepsItsSize()
        {
            RbxScreenGui screen = _world.Screen();
            RbxTextLabel label = Label(screen, "a long line of text", new RbxUDim2(0f, 20, 0f, 10), true);

            Assert.AreEqual(new RbxVector2(20f, 10f), label.AbsoluteSize);
            Assert.Greater(label.TextState.TextBounds.Y, 10f, "the text overflows; TextBounds says by how much");
        }

        [Test]
        public void TheHostsMeasurement_Wins_AndANewOneIsTakenOnceMoreOnTheNextFrame()
        {
            RbxScreenGui screen = _world.Screen();
            RbxTextLabel label = Label(screen, "hi", new RbxUDim2(0f, 100, 0f, 0), true, RbxAutomaticSize.Y);
            _world.Frame();
            FakeRbxUiBackend.FakeElement element = _world.Element(label);
            float height = 7f;
            element.Measure = (text, size, width) => new RbxVector2(12f, height);

            label.TextState.SetText("hello");
            _world.Presenter.Flush();
            Assert.AreEqual(7f, label.AbsoluteSize.Y);
            int measured = element.MeasureCount;

            height = 9f;
            _world.Presenter.Flush();
            Assert.AreEqual(9f, label.AbsoluteSize.Y, "the next frame confirms with the resolved style");
            Assert.Greater(element.MeasureCount, measured);

            int settled = element.MeasureCount;
            _world.Presenter.Flush();
            _world.Presenter.Flush();
            Assert.AreEqual(settled, element.MeasureCount, "twin: an unchanged text is not measured again");
        }

        [Test]
        public void Negative_ALabelNobodyMeasures_IsNotMeasured_UntilAScriptReadsItsTextBounds()
        {
            RbxScreenGui screen = _world.Screen();
            RbxTextLabel label = Label(screen, "hi", new RbxUDim2(0f, 100, 0f, 20));
            _world.Frame();
            FakeRbxUiBackend.FakeElement element = _world.Element(label);
            element.Measure = (text, size, width) => new RbxVector2(text.Length * 3f, 11f);

            label.TextState.SetText("a new text every frame");
            _world.Presenter.Flush();
            Assert.AreEqual(0, element.MeasureCount, "a new text of a plain label is a repaint");

            Assert.AreEqual(new RbxVector2(66f, 11f), label.TextState.TextBounds, "twin: the first read measures");
            label.TextState.SetText("shorter");
            _world.Presenter.Flush();
            Assert.AreEqual(new RbxVector2(21f, 11f), label.TextState.TextBounds, "and every later text is measured");
        }

        [Test]
        public void Estimate_BreaksAtSpaces_AtNewLines_AndMidWordWhenAWordIsTooLong()
        {
            Assert.AreEqual(new RbxVector2(0f, 10f), RbxTextMetrics.Estimate("", 10f, 0f, 1f));
            Assert.AreEqual(new RbxVector2(15f, 20f), RbxTextMetrics.Estimate("abc\nde", 10f, 0f, 1f));
            Assert.AreEqual(new RbxVector2(20f, 30f), RbxTextMetrics.Estimate("abcdefghij", 10f, 20f, 1f),
                "four characters a line, the word is split");
            Assert.AreEqual(new RbxVector2(25f, 30f), RbxTextMetrics.Estimate("ab cd ef", 10f, 25f, 1.5f),
                "LineHeight scales the line");
        }
    }
}
