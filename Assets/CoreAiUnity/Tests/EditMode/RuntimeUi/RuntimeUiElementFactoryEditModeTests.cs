using System.Collections.Generic;
using CoreAI.RuntimeUi;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode.RuntimeUi
{
    /// <summary>
    /// The Unity element factory: every supported UXML element becomes the right UI Toolkit control with
    /// its attributes applied, unknown elements become labelled placeholders, and the same kinds and
    /// attributes are reachable without markup.
    /// </summary>
    [TestFixture]
    public sealed class RuntimeUiElementFactoryEditModeTests
    {
        private const string EveryElementUxml =
            "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n" +
            "  <ui:VisualElement name=\"box\" class=\"a b\" tooltip=\"tip\" picking-mode=\"Ignore\"/>\n" +
            "  <ui:Label name=\"label\" text=\"Score\" enable-rich-text=\"false\"/>\n" +
            "  <ui:Button name=\"button\" text=\"Go\"/>\n" +
            "  <ui:Toggle name=\"toggle\" label=\"Sound\" value=\"true\" text=\"on\"/>\n" +
            "  <ui:Slider name=\"slider\" low-value=\"0\" high-value=\"20\" value=\"5\" direction=\"Vertical\" inverted=\"true\"/>\n" +
            "  <ui:TextField name=\"field\" label=\"Name\" value=\"Steve\" max-length=\"12\" placeholder-text=\"type\"/>\n" +
            "  <ui:ProgressBar name=\"progress\" low-value=\"0\" high-value=\"10\" value=\"7\" title=\"HP\"/>\n" +
            "  <ui:Image name=\"image\" scale-mode=\"ScaleToFit\" tint-color=\"#ff0000\"/>\n" +
            "  <ui:ScrollView name=\"scroll\" mode=\"Horizontal\" vertical-scroller-visibility=\"Hidden\">\n" +
            "    <ui:Label name=\"inside\"/>\n" +
            "  </ui:ScrollView>\n" +
            "  <ui:ListView name=\"list\" fixed-item-height=\"24\" selection-type=\"Multiple\" show-border=\"true\"/>\n" +
            "</ui:UXML>";

        [Test]
        public void Build_EverySupportedElement_CreatesTheRightControlWithItsAttributes()
        {
            RuntimeUiElementFactory factory = new(new RuntimeUiAssetRegistry(false));
            UxmlDocument document = UxmlParser.Parse(EveryElementUxml);
            Dictionary<VisualElement, RuntimeUiAuthoredElement> authored = new();
            RuntimeUiDiagnosticList diagnostics = new();
            VisualElement root = new();

            for (int i = 0; i < document.Root.Children.Count; i++)
            {
                root.Add(factory.Build(document.Root.Children[i], authored, diagnostics));
            }

            Assert.AreEqual(0, diagnostics.Count, string.Join("\n", diagnostics.Items));
            VisualElement box = root.Q<VisualElement>("box");
            Assert.AreEqual(typeof(VisualElement), box.GetType());
            Assert.IsTrue(box.ClassListContains("a") && box.ClassListContains("b"));
            Assert.AreEqual("tip", box.tooltip);
            Assert.AreEqual(PickingMode.Ignore, box.pickingMode);
            Assert.AreEqual("Score", root.Q<Label>("label").text);
            Assert.IsFalse(root.Q<Label>("label").enableRichText);
            Assert.AreEqual("Go", root.Q<Button>("button").text);
            Toggle toggle = root.Q<Toggle>("toggle");
            Assert.IsTrue(toggle.value);
            Assert.AreEqual("Sound", toggle.label);
            Assert.AreEqual("on", toggle.text);
            Slider slider = root.Q<Slider>("slider");
            Assert.AreEqual(20f, slider.highValue);
            Assert.AreEqual(5f, slider.value);
            Assert.AreEqual(SliderDirection.Vertical, slider.direction);
            Assert.IsTrue(slider.inverted);
            TextField field = root.Q<TextField>("field");
            Assert.AreEqual("Steve", field.value);
            Assert.AreEqual(12, field.maxLength);
            Assert.AreEqual("type", field.textEdition.placeholder);
            ProgressBar progress = root.Q<ProgressBar>("progress");
            Assert.AreEqual(7f, progress.value);
            Assert.AreEqual("HP", progress.title);
            Image image = root.Q<Image>("image");
            Assert.AreEqual(UnityEngine.ScaleMode.ScaleToFit, image.scaleMode);
            Assert.AreEqual(1f, image.tintColor.r);
            ScrollView scroll = root.Q<ScrollView>("scroll");
            Assert.AreEqual(ScrollViewMode.Horizontal, scroll.mode);
            Assert.AreEqual(ScrollerVisibility.Hidden, scroll.verticalScrollerVisibility);
            Assert.AreSame(scroll.contentContainer, root.Q<Label>("inside").hierarchy.parent,
                "ScrollView children go into its content container, as with the editor importer.");
            ListView list = root.Q<ListView>("list");
            Assert.AreEqual(24f, list.fixedItemHeight);
            Assert.AreEqual(SelectionType.Multiple, list.selectionType);
            Assert.IsTrue(list.showBorder);
            Assert.AreEqual(11, authored.Count, "Every authored element is recorded.");
        }

        [Test]
        public void Build_UnknownElement_IsAPlaceholderNamingWhatItReplaces_AndKeepsItsChildren()
        {
            RuntimeUiElementFactory factory = new(null);
            UxmlDocument document = UxmlParser.Parse("<ui:Foldout name=\"inv\"><ui:Label name=\"kept\"/></ui:Foldout>");
            Dictionary<VisualElement, RuntimeUiAuthoredElement> authored = new();

            VisualElement placeholder = factory.Build(document.Root.Children[0], authored, null);

            Assert.IsTrue(placeholder.ClassListContains(RuntimeUiElementFactory.PlaceholderClass));
            Assert.AreEqual("inv", placeholder.name);
            Label label = placeholder.Q<Label>(className: RuntimeUiElementFactory.PlaceholderLabelClass);
            StringAssert.Contains("Foldout", label.text);
            Assert.IsNotNull(placeholder.Q<Label>("kept"));
            Assert.AreEqual(RuntimeUiElementKind.Placeholder, authored[placeholder].Kind);
        }

        [TestCase(RuntimeUiElementKind.VisualElement, typeof(VisualElement))]
        [TestCase(RuntimeUiElementKind.Label, typeof(Label))]
        [TestCase(RuntimeUiElementKind.Button, typeof(Button))]
        [TestCase(RuntimeUiElementKind.Toggle, typeof(Toggle))]
        [TestCase(RuntimeUiElementKind.Slider, typeof(Slider))]
        [TestCase(RuntimeUiElementKind.TextField, typeof(TextField))]
        [TestCase(RuntimeUiElementKind.ProgressBar, typeof(ProgressBar))]
        [TestCase(RuntimeUiElementKind.Image, typeof(Image))]
        [TestCase(RuntimeUiElementKind.ScrollView, typeof(ScrollView))]
        [TestCase(RuntimeUiElementKind.ListView, typeof(ListView))]
        public void Create_EveryKind_WithoutMarkup(RuntimeUiElementKind kind, System.Type expected)
        {
            RuntimeUiElementFactory factory = new(null);

            VisualElement element = factory.Create(kind);

            Assert.AreEqual(expected, element.GetType());
            Assert.AreEqual(expected.Name, UxmlElementCatalog.TypeNameOf(kind));
        }

        [Test]
        public void ApplyAttribute_UsesTheUxmlNamesWithoutMarkup_AndRefusesUnknownOnes()
        {
            RuntimeUiElementFactory factory = new(null);
            Slider slider = (Slider)factory.Create(RuntimeUiElementKind.Slider);

            Assert.IsTrue(factory.ApplyAttribute(slider, RuntimeUiElementKind.Slider, "high-value", "50", out _));
            Assert.IsFalse(factory.ApplyAttribute(slider, RuntimeUiElementKind.Slider, "high-value", "lots",
                out string invalid));
            Assert.IsFalse(factory.ApplyAttribute(slider, RuntimeUiElementKind.Slider, "text", "x", out string unknown));

            Assert.AreEqual(50f, slider.highValue);
            StringAssert.Contains("not a number", invalid);
            StringAssert.Contains("not an attribute", unknown);
        }

        [Test]
        public void ImageAttribute_ResolvesThroughTheAssetRegistry_AndAnUnknownImageIsReported()
        {
            RuntimeUiAssetRegistry assets = new(false);
            UnityEngine.Texture2D texture = new(2, 2);
            assets.Register("sword", texture);
            RuntimeUiElementFactory factory = new(assets);
            UxmlDocument document = UxmlParser.Parse("<ui:Image image=\"sword\"/><ui:Image image=\"shield\"/>");
            Dictionary<VisualElement, RuntimeUiAuthoredElement> authored = new();
            RuntimeUiDiagnosticList diagnostics = new();

            Image known = (Image)factory.Build(document.Root.Children[0], authored, diagnostics);
            factory.Build(document.Root.Children[1], authored, diagnostics);

            Assert.AreSame(texture, known.image);
            Assert.AreEqual(1, diagnostics.Count, "The unknown image is reported once.");
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
