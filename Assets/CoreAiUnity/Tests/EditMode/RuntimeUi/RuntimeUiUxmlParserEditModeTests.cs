using System.Collections.Generic;
using CoreAI.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RuntimeUi
{
    /// <summary>
    /// The engine-free UXML reader: the tree it builds, what it reports, and that broken markup degrades
    /// instead of throwing into the game loop. Runs in Unity EditMode and in the portable suite.
    /// </summary>
    [TestFixture]
    public sealed class RuntimeUiUxmlParserEditModeTests
    {
        [Test]
        public void Parse_WellFormedScreen_BuildsNamesClassesAttributesAndNoReports()
        {
            const string uxml =
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n" +
                "  <ui:VisualElement name=\"hud\" class=\"cai-panel  row\">\n" +
                "    <ui:Label name=\"title\" text=\"Hello &amp; welcome\"/>\n" +
                "    <ui:Button name=\"ok\" class=\"cai-btn\" text=\"OK\"/>\n" +
                "    <ui:Slider name=\"volume\" low-value=\"0\" high-value=\"10\" value=\"5\" direction=\"vertical\"/>\n" +
                "  </ui:VisualElement>\n" +
                "</ui:UXML>";

            UxmlDocument document = UxmlParser.Parse(uxml);

            Assert.AreEqual(0, document.Diagnostics.Count, string.Join("\n", document.Diagnostics));
            Assert.AreEqual(4, document.ElementCount);
            UxmlNode hud = document.Root.Children[0];
            Assert.AreEqual(RuntimeUiElementKind.VisualElement, hud.Kind);
            Assert.AreEqual("hud", hud.Name);
            CollectionAssert.AreEqual(new[] { "cai-panel", "row" }, hud.Classes);
            Assert.AreEqual("Hello & welcome", hud.Children[0].GetAttribute("text"));
            Assert.AreEqual(RuntimeUiElementKind.Button, hud.Children[1].Kind);
            UxmlNode slider = hud.Children[2];
            Assert.AreEqual(RuntimeUiElementKind.Slider, slider.Kind);
            Assert.AreEqual("Vertical", slider.GetAttribute("direction"), "Enum attributes are case-insensitive.");
            Assert.AreEqual(5, slider.Line);
            Assert.AreSame(hud, slider.Parent);
        }

        [Test]
        public void Parse_ElementsWithoutNamespaceDeclarations_StillResolveToEngineElements()
        {
            UxmlDocument document = UxmlParser.Parse("<Button text=\"a\"/><ui:Label text=\"b\"/><engine:Toggle/>");

            Assert.AreEqual(0, document.Diagnostics.Count, string.Join("\n", document.Diagnostics));
            Assert.AreEqual(RuntimeUiElementKind.Button, document.Root.Children[0].Kind);
            Assert.AreEqual(RuntimeUiElementKind.Label, document.Root.Children[1].Kind);
            Assert.AreEqual(RuntimeUiElementKind.Toggle, document.Root.Children[2].Kind);
        }

        [Test]
        public void Parse_FullyQualifiedTypeName_Resolves()
        {
            UxmlDocument document = UxmlParser.Parse("<UnityEngine.UIElements.ProgressBar value=\"3\"/>");

            Assert.AreEqual(RuntimeUiElementKind.ProgressBar, document.Root.Children[0].Kind);
            Assert.AreEqual(0, document.Diagnostics.Count);
        }

        [Test]
        public void Parse_UnknownElement_BecomesPlaceholderKeepsChildrenAndReportsItsLineOnce()
        {
            const string uxml =
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n" +
                "  <ui:VisualElement>\n" +
                "    <ui:Foldout name=\"inventory\">\n" +
                "      <ui:Label name=\"inside\" text=\"kept\"/>\n" +
                "    </ui:Foldout>\n" +
                "  </ui:VisualElement>\n" +
                "</ui:UXML>";

            UxmlDocument document = UxmlParser.Parse(uxml);

            UxmlNode placeholder = document.Root.Children[0].Children[0];
            Assert.IsTrue(placeholder.IsPlaceholder);
            Assert.AreEqual("Foldout", placeholder.ElementType);
            Assert.AreEqual("inventory", placeholder.Name, "The placeholder keeps the name so queries still find it.");
            Assert.AreEqual("inside", placeholder.Children[0].Name);
            Assert.AreEqual(1, document.Diagnostics.Count, string.Join("\n", document.Diagnostics));
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UxmlUnknownElement, document.Diagnostics[0].Code);
            Assert.AreEqual(3, document.Diagnostics[0].Line);
        }

        [Test]
        public void Parse_EditorOnlyElement_IsReportedAsEditorOnly()
        {
            UxmlDocument document = UxmlParser.Parse(
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\"><uie:ObjectField/></ui:UXML>");

            Assert.IsTrue(document.Root.Children[0].IsPlaceholder);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UxmlEditorOnlyElement, document.Diagnostics[0].Code);
        }

        [Test]
        public void Parse_CustomNamespaceElement_IsAPlaceholder()
        {
            UxmlDocument document = UxmlParser.Parse("<ui:UXML xmlns:my=\"MyGame.Ui\"><my:HealthBar/></ui:UXML>");

            Assert.IsTrue(document.Root.Children[0].IsPlaceholder);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UxmlUnknownElement, document.Diagnostics[0].Code);
        }

        [Test]
        public void Parse_UnclosedElement_KeepsItsChildrenAndReports()
        {
            UxmlDocument document = UxmlParser.Parse("<ui:VisualElement name=\"a\">\n<ui:Label name=\"b\"/>");

            Assert.AreEqual("a", document.Root.Children[0].Name);
            Assert.AreEqual("b", document.Root.Children[0].Children[0].Name);
            Assert.AreEqual(1, CountOf(document, RuntimeUiDiagnosticCodes.UxmlUnclosedElement));
            Assert.AreEqual(1, document.Diagnostics[0].Line);
        }

        [Test]
        public void Parse_StrayClosingTag_IsIgnoredAndReported()
        {
            UxmlDocument document = UxmlParser.Parse("<ui:Label name=\"a\"/></ui:Button><ui:Label name=\"b\"/>");

            Assert.AreEqual(2, document.Root.Children.Count);
            Assert.AreEqual(1, CountOf(document, RuntimeUiDiagnosticCodes.UxmlMismatchedCloseTag));
        }

        [Test]
        public void Parse_ClosingAnOuterElement_ClosesTheInnerOneAndReportsIt()
        {
            UxmlDocument document = UxmlParser.Parse(
                "<ui:VisualElement name=\"outer\"><ui:Label name=\"inner\"></ui:VisualElement><ui:Label name=\"after\"/>");

            Assert.AreEqual("outer", document.Root.Children[0].Name);
            Assert.AreEqual("inner", document.Root.Children[0].Children[0].Name);
            Assert.AreEqual("after", document.Root.Children[1].Name);
            Assert.AreEqual(1, CountOf(document, RuntimeUiDiagnosticCodes.UxmlUnclosedElement));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("<<<>>>&&&")]
        [TestCase("<ui:Button text=")]
        [TestCase("<ui:Button text=\"never closed")]
        [TestCase("<!-- comment that never ends")]
        [TestCase("</>")]
        [TestCase("<ui:VisualElement><ui:VisualElement><ui:Label")]
        [TestCase("<![CDATA[ unterminated")]
        [TestCase("<?xml version=\"1.0\"?><ui:UXML><ui:Label text=\"&#99999999;\"/></ui:UXML>")]
        [TestCase("<a b c d e f g/><1 2 3>")]
        public void Parse_MalformedMarkup_NeverThrows(string uxml)
        {
            UxmlDocument document = null;

            Assert.DoesNotThrow(() => document = UxmlParser.Parse(uxml));

            Assert.IsNotNull(document);
            Assert.IsNotNull(document.Root);
        }

        [Test]
        public void Parse_UnquotedAttribute_IsReadWithAWarning()
        {
            UxmlDocument document = UxmlParser.Parse("<ui:Label name=title text=\"x\"/>");

            Assert.AreEqual("title", document.Root.Children[0].Name);
            Assert.AreEqual(1, CountOf(document, RuntimeUiDiagnosticCodes.UxmlMalformed));
        }

        [Test]
        public void Parse_UnsupportedAttribute_ReportsOnce_AndASupportedOneReportsNothing()
        {
            UxmlDocument unsupported = UxmlParser.Parse("<ui:Button text=\"a\" icon-image=\"x.png\"/>");
            UxmlDocument supported = UxmlParser.Parse("<ui:Button text=\"a\" tooltip=\"b\"/>");

            Assert.AreEqual(1, unsupported.Diagnostics.Count);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UxmlUnsupportedAttribute, unsupported.Diagnostics[0].Code);
            Assert.IsNull(unsupported.Root.Children[0].GetAttribute("icon-image"));
            Assert.AreEqual(0, supported.Diagnostics.Count, string.Join("\n", supported.Diagnostics));
        }

        [Test]
        public void Parse_InvalidAttributeValue_IsReportedAndDropped()
        {
            UxmlDocument document = UxmlParser.Parse("<ui:Slider low-value=\"lots\" high-value=\"10\"/>");

            Assert.AreEqual(1, CountOf(document, RuntimeUiDiagnosticCodes.UxmlInvalidAttributeValue));
            Assert.IsNull(document.Root.Children[0].GetAttribute("low-value"));
            Assert.AreEqual("10", document.Root.Children[0].GetAttribute("high-value"));
        }

        [Test]
        public void Parse_TextContentOfALabel_BecomesItsTextWithAnInfo()
        {
            UxmlDocument document = UxmlParser.Parse("<ui:Label>\n   Score\n</ui:Label><ui:VisualElement>lost</ui:VisualElement>");

            Assert.AreEqual("Score", document.Root.Children[0].GetAttribute("text"));
            Assert.AreEqual(2, CountOf(document, RuntimeUiDiagnosticCodes.UxmlTextContent));
            Assert.AreEqual(RuntimeUiDiagnosticSeverity.Info, document.Diagnostics[0].Severity);
            Assert.AreEqual(RuntimeUiDiagnosticSeverity.Warning, document.Diagnostics[1].Severity);
        }

        [Test]
        public void Parse_StyleAttribute_BecomesDeclarations_AndUnsupportedOnesAreReported()
        {
            UxmlDocument document = UxmlParser.Parse("<ui:Label style=\"width: 10px; cursor: link; color: red\"/>");

            UxmlNode label = document.Root.Children[0];
            Assert.AreEqual(2, label.InlineStyle.Count);
            Assert.AreEqual("width", label.InlineStyle[0].Property);
            Assert.AreEqual(1, document.Diagnostics.Count);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UssUnsupportedProperty, document.Diagnostics[0].Code);
            Assert.AreEqual(RuntimeUiSource.InlineStyle, document.Diagnostics[0].Source);
        }

        [Test]
        public void Parse_StyleReference_IsReportedAsIgnored()
        {
            UxmlDocument document = UxmlParser.Parse("<ui:UXML><Style src=\"hud.uss\"/><ui:Label/></ui:UXML>");

            Assert.AreEqual(1, document.Root.Children.Count);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UxmlStyleReferenceIgnored, document.Diagnostics[0].Code);
        }

        [Test]
        public void Parse_TemplateInstance_ExpandsWithAttributeOverrides()
        {
            UxmlTemplateRegistry registry = new();
            registry.Register("Slot",
                "<ui:UXML><ui:VisualElement class=\"slot\"><ui:Label name=\"count\" text=\"0\"/></ui:VisualElement></ui:UXML>");
            const string uxml =
                "<ui:UXML>\n" +
                "  <ui:Template name=\"SlotTemplate\" src=\"UI/Slot.uxml\"/>\n" +
                "  <ui:Instance template=\"SlotTemplate\" name=\"slot1\" class=\"first\">\n" +
                "    <ui:AttributeOverrides element-name=\"count\" text=\"5\"/>\n" +
                "  </ui:Instance>\n" +
                "  <ui:Instance template=\"SlotTemplate\" name=\"slot2\"/>\n" +
                "</ui:UXML>";

            UxmlDocument document = UxmlParser.Parse(uxml, new UxmlParseOptions { TemplateResolver = registry });

            Assert.AreEqual(0, document.Diagnostics.Count, string.Join("\n", document.Diagnostics));
            UxmlNode first = document.Root.Children[0];
            Assert.AreEqual(RuntimeUiElementKind.TemplateContainer, first.Kind);
            Assert.AreEqual("slot1", first.Name);
            CollectionAssert.Contains(first.Classes, "first");
            Assert.AreEqual("5", first.Children[0].Children[0].GetAttribute("text"));
            Assert.AreEqual("0", document.Root.Children[1].Children[0].Children[0].GetAttribute("text"),
                "Overrides apply to their own instance only.");
            Assert.AreEqual(6, document.ElementCount, "Two containers with two elements each, counted once.");
        }

        [Test]
        public void Parse_MissingTemplate_IsReported()
        {
            UxmlDocument document = UxmlParser.Parse("<ui:Instance template=\"Nope\"/>",
                new UxmlParseOptions { TemplateResolver = new UxmlTemplateRegistry() });

            Assert.AreEqual(1, CountOf(document, RuntimeUiDiagnosticCodes.UxmlTemplateNotFound));
        }

        [Test]
        public void Parse_RecursiveTemplate_IsReportedInsteadOfLoopingForever()
        {
            UxmlTemplateRegistry registry = new();
            registry.Register("Loop", "<ui:Instance template=\"Loop\"/>");

            UxmlDocument document = UxmlParser.Parse("<ui:Instance template=\"Loop\"/>",
                new UxmlParseOptions { TemplateResolver = registry });

            Assert.GreaterOrEqual(CountOf(document, RuntimeUiDiagnosticCodes.UxmlTemplateRecursion), 1);
        }

        [Test]
        public void Parse_ListViewItemTemplate_IsResolved()
        {
            UxmlTemplateRegistry registry = new();
            registry.Register("Row", "<ui:Label class=\"row\"/>");

            UxmlDocument document = UxmlParser.Parse("<ui:ListView item-template=\"Row\" fixed-item-height=\"24\"/>",
                new UxmlParseOptions { TemplateResolver = registry });

            UxmlNode list = document.Root.Children[0];
            Assert.IsNotNull(list.ItemTemplate);
            Assert.AreEqual(RuntimeUiElementKind.Label, list.ItemTemplate.Children[0].Kind);
        }

        [Test]
        public void Parse_MoreElementsThanTheLimit_StopsAndReportsOnce()
        {
            System.Text.StringBuilder builder = new();
            for (int i = 0; i < 50; i++)
            {
                builder.Append("<ui:Label/>");
            }

            UxmlDocument document = UxmlParser.Parse(builder.ToString(),
                new UxmlParseOptions { Limits = new RuntimeUiParseLimits { MaxElements = 10 } });

            Assert.AreEqual(10, document.Root.Children.Count);
            Assert.AreEqual(1, CountOf(document, RuntimeUiDiagnosticCodes.UxmlLimitExceeded));
        }

        [Test]
        public void Parse_DeeperThanTheLimit_StopsDescendingAndReportsOnce()
        {
            System.Text.StringBuilder builder = new();
            for (int i = 0; i < 20; i++)
            {
                builder.Append("<ui:VisualElement>");
            }

            UxmlDocument document = UxmlParser.Parse(builder.ToString(),
                new UxmlParseOptions { Limits = new RuntimeUiParseLimits { MaxDepth = 5 } });

            Assert.AreEqual(5, document.ElementCount);
            Assert.AreEqual(1, CountOf(document, RuntimeUiDiagnosticCodes.UxmlLimitExceeded));
        }

        [Test]
        public void Parse_NumericEntities_AreDecoded()
        {
            UxmlDocument document = UxmlParser.Parse("<ui:Label text=\"&#65;&#x42;&lt;\"/>");

            Assert.AreEqual("AB<", document.Root.Children[0].GetAttribute("text"));
        }

        [Test]
        public void Parse_TwoHundredElementFixture_HasTwoHundredElements()
        {
            UxmlDocument document = UxmlParser.Parse(RuntimeUiFixtureTexts.TwoHundredElementUxml());

            Assert.AreEqual(200, document.ElementCount);
            Assert.AreEqual(0, document.Diagnostics.Count, string.Join("\n", document.Diagnostics));
        }

        private static int CountOf(UxmlDocument document, string code)
        {
            int count = 0;
            IReadOnlyList<RuntimeUiDiagnostic> diagnostics = document.Diagnostics;
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
