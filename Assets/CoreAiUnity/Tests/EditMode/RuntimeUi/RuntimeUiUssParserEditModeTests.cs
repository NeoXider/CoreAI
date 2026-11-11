using CoreAI.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RuntimeUi
{
    /// <summary>
    /// The engine-free USS subset parser: value grammar, shorthands, selectors and specificity, and the
    /// rule that nothing is dropped without a report naming its line. Runs in Unity EditMode and in the
    /// portable suite.
    /// </summary>
    [TestFixture]
    public sealed class RuntimeUiUssParserEditModeTests
    {
        [Test]
        public void Parse_SupportedSheet_ProducesRulesAndNoReports()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(RuntimeUiFixtureTexts.HotbarUss);

            Assert.AreEqual(0, sheet.Diagnostics.Count, string.Join("\n", sheet.Diagnostics));
            Assert.AreEqual(11, sheet.Rules.Count);
        }

        [Test]
        public void Parse_UnsupportedProperty_ReportsOnceWithItsLine_AndTheRestOfTheRuleApplies()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(".a {\n  width: 10px;\n  cursor: link;\n  color: red;\n}");

            Assert.AreEqual(1, sheet.Diagnostics.Count);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UssUnsupportedProperty, sheet.Diagnostics[0].Code);
            Assert.AreEqual(3, sheet.Diagnostics[0].Line);
            Assert.AreEqual(2, sheet.Rules[0].Declarations.Count);
        }

        [Test]
        public void Parse_SupportedTwinOfTheUnsupportedProperty_ReportsNothing()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(".a {\n  width: 10px;\n  opacity: 0.5;\n  color: red;\n}");

            Assert.AreEqual(0, sheet.Diagnostics.Count, string.Join("\n", sheet.Diagnostics));
            Assert.AreEqual(3, sheet.Rules[0].Declarations.Count);
        }

        [Test]
        public void Parse_NotAProperty_IsReported()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(".a { colour: red; }");

            Assert.AreEqual(RuntimeUiDiagnosticCodes.UssUnsupportedProperty, sheet.Diagnostics[0].Code);
            StringAssert.Contains("not a USS property", sheet.Diagnostics[0].Message);
        }

        [TestCase("width: 10em")]
        [TestCase("width: -4px")]
        [TestCase("display: grid")]
        [TestCase("color: rgb(1, 2)")]
        [TestCase("opacity: half")]
        [TestCase("transition-timing-function: bouncy")]
        public void Parse_InvalidValue_IsReportedAndDropped(string declaration)
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(".a { " + declaration + "; }");

            Assert.AreEqual(1, sheet.Diagnostics.Count, string.Join("\n", sheet.Diagnostics));
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UssInvalidValue, sheet.Diagnostics[0].Code);
            Assert.AreEqual(0, sheet.Rules[0].Declarations.Count);
        }

        [Test]
        public void Parse_UnsupportedSelectorInAGroup_DropsOnlyThatSelector()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(".a, .b + .c, .d:first-child, .e[x] { color: red; }");

            Assert.AreEqual(1, sheet.Rules.Count);
            Assert.AreEqual(1, sheet.Rules[0].Selectors.Count);
            Assert.AreEqual(".a", sheet.Rules[0].Selectors[0].Text);
            Assert.AreEqual(3, sheet.Diagnostics.Count, string.Join("\n", sheet.Diagnostics));
            for (int i = 0; i < sheet.Diagnostics.Count; i++)
            {
                Assert.AreEqual(RuntimeUiDiagnosticCodes.UssUnsupportedSelector, sheet.Diagnostics[i].Code);
            }
        }

        [Test]
        public void Parse_AtRules_AreReportedAndSkipped()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(
                "@import url(\"theme.uss\");\n@media screen { .a { color: red; } }\n.b { color: blue; }");

            Assert.AreEqual(1, sheet.Rules.Count);
            Assert.AreEqual(".b", sheet.Rules[0].Selectors[0].Text);
            Assert.AreEqual(2, sheet.Diagnostics.Count);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UssUnsupportedAtRule, sheet.Diagnostics[0].Code);
            Assert.AreEqual(1, sheet.Diagnostics[0].Line);
            Assert.AreEqual(2, sheet.Diagnostics[1].Line);
        }

        [Test]
        public void Parse_Important_IsReportedAndTheValueStillApplies()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(".a { color: red !important; }");

            Assert.AreEqual(RuntimeUiDiagnosticCodes.UssUnsupportedImportant, sheet.Diagnostics[0].Code);
            Assert.AreEqual("red", sheet.Rules[0].Declarations[0].RawValue);
        }

        [Test]
        public void Parse_MissingBrace_RecoversAtTheNextRule()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(".a color: red; }\n.b { color: blue; }");

            Assert.AreEqual(1, sheet.Rules.Count);
            Assert.AreEqual(".b", sheet.Rules[0].Selectors[0].Text);
            Assert.GreaterOrEqual(sheet.Diagnostics.Count, 1);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UssSyntax, sheet.Diagnostics[0].Code);
        }

        [Test]
        public void Parse_UnterminatedComment_IsReported()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(".a { color: red; } /* never closed");

            Assert.AreEqual(1, sheet.Rules.Count);
            Assert.AreEqual(RuntimeUiDiagnosticCodes.UssSyntax, sheet.Diagnostics[0].Code);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("{{{{")]
        [TestCase("}}}}")]
        [TestCase(".a { color: ")]
        [TestCase(".a { color: var(--x; }")]
        [TestCase("@media { ")]
        [TestCase("::::: { :;:;: }")]
        [TestCase(".a { b { c { d: e } } }")]
        [TestCase("\"unterminated { color: red }")]
        public void Parse_MalformedSheet_NeverThrows(string uss)
        {
            UssStyleSheet sheet = null;

            Assert.DoesNotThrow(() => sheet = UssStyleSheet.Parse(uss));

            Assert.IsNotNull(sheet);
        }

        [Test]
        public void Parse_CommentsAndStrings_DoNotBreakLineNumbers()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse("/* a\nmultiline\ncomment */\n.a {\n  bogus: 1;\n}");

            Assert.AreEqual(5, sheet.Diagnostics[0].Line);
        }

        [TestCase("#a", 100)]
        [TestCase(".b.c", 20)]
        [TestCase("Button", 1)]
        [TestCase("Button.c:hover", 21)]
        [TestCase("*", 0)]
        [TestCase("#hud > .slot .icon", 120)]
        public void Selector_Specificity_UsesUiToolkitWeights(string text, int expected)
        {
            Assert.IsTrue(UssComplexSelector.TryParse(text, 1, 1, out UssComplexSelector selector, out string error),
                error);

            Assert.AreEqual(expected, selector.Specificity);
        }

        [TestCase("#hotbar > .slot:hover", 2)]
        [TestCase("VisualElement .a.b", 2)]
        [TestCase("*:root", 1)]
        public void Selector_Parse_SplitsCompounds(string text, int compounds)
        {
            Assert.IsTrue(UssComplexSelector.TryParse(text, 1, 1, out UssComplexSelector selector, out string error),
                error);

            Assert.AreEqual(compounds, selector.Compounds.Length);
        }

        [Test]
        public void Shorthand_Margin_ExpandsTopRightBottomLeft()
        {
            UssValue[] values = Expand("margin", "1px 2px 3px");

            Assert.AreEqual(1f, values[0].A);
            Assert.AreEqual(2f, values[1].A);
            Assert.AreEqual(3f, values[2].A);
            Assert.AreEqual(2f, values[3].A);
        }

        [Test]
        public void Shorthand_FlexOne_IsGrowOneShrinkOneBasisZeroPercent()
        {
            UssValue[] values = Expand("flex", "1");

            Assert.AreEqual(1f, values[0].A);
            Assert.AreEqual(1f, values[1].A);
            Assert.AreEqual(UssValueType.Length, values[2].Type);
            Assert.AreEqual(UssUnit.Percent, values[2].UnitA);
            Assert.AreEqual(0f, values[2].A);
        }

        [Test]
        public void Shorthand_Transition_SplitsIntoFourLists()
        {
            UssValue[] values = Expand("transition", "opacity 0.3s ease-in, scale 200ms linear 0.1s");

            Assert.AreEqual(2, values[0].ListItems.Length);
            Assert.AreEqual("opacity", values[0].ListItems[0].Keyword);
            Assert.AreEqual(0.2f, values[1].ListItems[1].A, 1e-5f);
            Assert.AreEqual("linear", values[2].ListItems[1].Keyword);
            Assert.AreEqual(0.1f, values[3].ListItems[1].A, 1e-5f);
        }

        [Test]
        public void Shorthand_BorderRadius_FourCorners()
        {
            UssValue[] values = Expand("border-radius", "1px 2px 3px 4px");

            Assert.AreEqual(4f, values[3].A);
        }

        [TestCase("#fff", 1f, 1f, 1f, 1f)]
        [TestCase("#ff000080", 1f, 0f, 0f, 0.50196f)]
        [TestCase("rgba(255, 0, 0, 0.5)", 1f, 0f, 0f, 0.5f)]
        [TestCase("rgb(0 255 0)", 0f, 1f, 0f, 1f)]
        [TestCase("gold", 1f, 0.84314f, 0f, 1f)]
        [TestCase("transparent", 0f, 0f, 0f, 0f)]
        public void Value_Colors_Parse(string text, float r, float g, float b, float a)
        {
            Assert.IsTrue(UssValueParser.TryParseColor(text, out UssValue value));

            Assert.AreEqual(r, value.A, 1e-4f);
            Assert.AreEqual(g, value.B, 1e-4f);
            Assert.AreEqual(b, value.C, 1e-4f);
            Assert.AreEqual(a, value.D, 1e-4f);
        }

        [TestCase("width", "10", 10f, UssUnit.Pixel)]
        [TestCase("width", "10px", 10f, UssUnit.Pixel)]
        [TestCase("width", "50%", 50f, UssUnit.Percent)]
        [TestCase("left", "-4px", -4f, UssUnit.Pixel)]
        public void Value_Lengths_Parse(string property, string text, float expected, UssUnit unit)
        {
            Assert.IsTrue(UssPropertyCatalog.TryGetLonghand(property, out UssPropertySpec spec));
            Assert.IsTrue(UssValueParser.TryParseLonghand(spec, text, out UssValue value, out string error), error);

            Assert.AreEqual(expected, value.A);
            Assert.AreEqual(unit, value.UnitA);
        }

        [Test]
        public void Value_Initial_IsAcceptedForAnyProperty()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(".a { width: initial; color: initial; margin: initial; }");

            Assert.AreEqual(0, sheet.Diagnostics.Count, string.Join("\n", sheet.Diagnostics));
        }

        [Test]
        public void InlineDeclarations_ReportWithTheirLineInsideTheDocument()
        {
            RuntimeUiDiagnosticList diagnostics = new();

            UssDeclaration[] declarations = UssParser.ParseInlineDeclarations("width: 4px;\nbogus: 1", 7,
                RuntimeUiSource.InlineStyle, diagnostics);

            Assert.AreEqual(1, declarations.Length);
            Assert.AreEqual(8, diagnostics.Items[0].Line);
        }

        private static UssValue[] Expand(string shorthand, string text)
        {
            Assert.IsTrue(UssPropertyCatalog.TryGetShorthand(shorthand, out UssShorthandSpec spec));
            UssValue[] values = new UssValue[spec.Longhands.Length];
            Assert.IsTrue(UssValueParser.TryExpandShorthand(spec, text, values, out string error), error);
            return values;
        }
    }
}
