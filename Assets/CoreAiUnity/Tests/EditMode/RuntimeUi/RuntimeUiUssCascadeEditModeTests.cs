using System.Collections.Generic;
using System.Diagnostics;
using CoreAI.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RuntimeUi
{
    /// <summary>
    /// The engine-free cascade: selector matching, specificity and order, theme-versus-screen tiers,
    /// inline and API layers, interaction states, custom properties and <c>var()</c>, and the reports it
    /// raises once per broken declaration. Runs in Unity EditMode and in the portable suite.
    /// </summary>
    [TestFixture]
    public sealed class RuntimeUiUssCascadeEditModeTests
    {
        [Test]
        public void HigherSpecificity_Wins_RegardlessOfSourceOrder()
        {
            FakeCascade cascade = Cascade("#ok { width: 30px; } .btn { width: 20px; } Button { width: 10px; }");
            FakeUssElement button = new("Button", "ok", "btn");

            UssResolvedStyle style = cascade.Resolve(button);

            Assert.AreEqual(30f, style.Get(UssPropertyId.Width).A);
        }

        [Test]
        public void EqualSpecificity_LaterRuleWins()
        {
            FakeCascade cascade = Cascade(".a { color: red; } .b { color: blue; }");

            UssResolvedStyle style = cascade.Resolve(new FakeUssElement("Label", null, "a", "b"));

            Assert.AreEqual(1f, style.Get(UssPropertyId.Color).C, "Blue comes later, so blue wins.");
        }

        [Test]
        public void ThemeSheet_LosesToTheScreenSheet_EvenWithHigherSpecificity()
        {
            FakeCascade cascade = new();
            cascade.Sheets.Add(UssStyleSheet.Parse("#title.big { font-size: 40px; }"));
            cascade.Sheets.Add(UssStyleSheet.Parse("Label { font-size: 12px; }"));
            cascade.DefaultSheetCount = 1;

            UssResolvedStyle style = cascade.Resolve(new FakeUssElement("Label", "title", "big"));

            Assert.AreEqual(12f, style.Get(UssPropertyId.FontSize).A);
        }

        [Test]
        public void InlineStyle_BeatsRules_AndApiOverrides_BeatInlineStyle()
        {
            FakeCascade cascade = Cascade("#a { width: 10px; height: 10px; opacity: 0.1; }");
            UssDeclaration[] inline = UssParser.ParseInlineDeclarations("width: 20px; height: 20px", 1,
                RuntimeUiSource.InlineStyle, null);
            UssDeclaration[] overrides = { UssDeclaration.FromValue(UssPropertyId.Height, UssValue.FromLength(30f)) };

            UssResolvedStyle style = cascade.Resolve(new FakeUssElement("VisualElement", "a"), overrides, inline);

            Assert.AreEqual(20f, style.Get(UssPropertyId.Width).A);
            Assert.AreEqual(30f, style.Get(UssPropertyId.Height).A);
            Assert.AreEqual(0.1f, style.Get(UssPropertyId.Opacity).A, 1e-6f);
        }

        [Test]
        public void DescendantAndChildCombinators_MatchLikeUiToolkit()
        {
            FakeCascade cascade = Cascade(".bar > .icon { width: 1px; } .bar .count { width: 2px; }");
            FakeUssElement bar = new("VisualElement", null, "bar");
            FakeUssElement slot = bar.Add(new FakeUssElement("VisualElement", null, "slot"));
            FakeUssElement directIcon = bar.Add(new FakeUssElement("VisualElement", null, "icon"));
            FakeUssElement nestedIcon = slot.Add(new FakeUssElement("VisualElement", null, "icon"));
            FakeUssElement nestedCount = slot.Add(new FakeUssElement("Label", null, "count"));

            Assert.AreEqual(1f, cascade.Resolve(directIcon).Get(UssPropertyId.Width).A);
            Assert.IsFalse(cascade.Resolve(nestedIcon).IsSet(UssPropertyId.Width), "'>' only matches the parent.");
            Assert.AreEqual(2f, cascade.Resolve(nestedCount).Get(UssPropertyId.Width).A);
        }

        [Test]
        public void HoverRule_AppliesOnlyWhileTheStateIsSet()
        {
            FakeCascade cascade = Cascade(".slot { opacity: 0.5; } .slot:hover { opacity: 1; }");
            FakeUssElement slot = new("VisualElement", null, "slot");

            Assert.AreEqual(0.5f, cascade.Resolve(slot).Get(UssPropertyId.Opacity).A);
            slot.PseudoStates = UssPseudoState.Hover;
            Assert.AreEqual(1f, cascade.Resolve(slot).Get(UssPropertyId.Opacity).A);
        }

        [Test]
        public void StateOnAnAncestor_RestylesTheDescendant()
        {
            FakeCascade cascade = Cascade(".slot:hover > .icon { scale: 1.2; }");
            FakeUssElement slot = new("VisualElement", null, "slot");
            FakeUssElement icon = slot.Add(new FakeUssElement("VisualElement", null, "icon"));

            Assert.IsFalse(cascade.Resolve(icon).IsSet(UssPropertyId.Scale));
            slot.PseudoStates = UssPseudoState.Hover;
            Assert.AreEqual(1.2f, cascade.Resolve(icon).Get(UssPropertyId.Scale).A, 1e-6f);
        }

        [Test]
        public void ActiveFocusDisabledChecked_AndTheirNegations_Match()
        {
            FakeCascade cascade = Cascade(
                "Button:active { width: 1px; } TextField:focus { width: 2px; } .x:disabled { width: 3px; } " +
                "Toggle:checked { width: 4px; } .y:enabled { height: 5px; } .z:inactive { height: 6px; }");

            Assert.AreEqual(1f, cascade.Resolve(WithState("Button", UssPseudoState.Active)).Get(UssPropertyId.Width).A);
            Assert.AreEqual(2f, cascade.Resolve(WithState("TextField", UssPseudoState.Focus)).Get(UssPropertyId.Width).A);
            Assert.AreEqual(3f, cascade.Resolve(WithState("VisualElement", UssPseudoState.Disabled, "x"))
                .Get(UssPropertyId.Width).A);
            Assert.AreEqual(4f, cascade.Resolve(WithState("Toggle", UssPseudoState.Checked)).Get(UssPropertyId.Width).A);
            Assert.AreEqual(5f, cascade.Resolve(WithState("VisualElement", UssPseudoState.None, "y"))
                .Get(UssPropertyId.Height).A);
            Assert.IsFalse(cascade.Resolve(WithState("VisualElement", UssPseudoState.Disabled, "y"))
                .IsSet(UssPropertyId.Height));
            Assert.AreEqual(6f, cascade.Resolve(WithState("VisualElement", UssPseudoState.None, "z"))
                .Get(UssPropertyId.Height).A);
        }

        [Test]
        public void TrackedStates_AreOnlyThoseSomeCompoundCanTest()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(".slot:hover > .icon { width: 1px; } Button:active { width: 2px; }");

            Assert.AreEqual(UssPseudoState.Hover, sheet.TrackedStatesFor(new FakeUssElement("VisualElement", null, "slot")));
            Assert.AreEqual(UssPseudoState.Active, sheet.TrackedStatesFor(new FakeUssElement("Button")));
            Assert.AreEqual(UssPseudoState.None, sheet.TrackedStatesFor(new FakeUssElement("Label")));
        }

        [Test]
        public void Root_MatchesTheScreenRootOnly()
        {
            FakeCascade cascade = Cascade(":root { opacity: 0.7; }");
            FakeUssElement root = new("VisualElement") { PseudoStates = UssPseudoState.Root };
            FakeUssElement child = root.Add(new FakeUssElement("VisualElement"));

            Assert.AreEqual(0.7f, cascade.Resolve(root).Get(UssPropertyId.Opacity).A, 1e-6f);
            Assert.IsFalse(cascade.Resolve(child).IsSet(UssPropertyId.Opacity));
        }

        [Test]
        public void Var_ResolvesFromAnAncestorsCustomProperty()
        {
            FakeCascade cascade = Cascade(".panel { --accent: #ff0000; } .label { color: var(--accent); }");
            FakeUssElement panel = new("VisualElement", null, "panel");
            FakeUssElement label = panel.Add(new FakeUssElement("Label", null, "label"));

            UssResolvedStyle style = cascade.Resolve(label);

            Assert.AreEqual(1f, style.Get(UssPropertyId.Color).A);
            Assert.AreEqual(0, cascade.Diagnostics.Count, string.Join("\n", cascade.Diagnostics.Items));
        }

        [Test]
        public void Var_FallbackIsUsedWhenTheCustomPropertyIsUndefined()
        {
            FakeCascade cascade = Cascade(".a { width: var(--missing, 12px); }");

            Assert.AreEqual(12f, cascade.Resolve(new FakeUssElement("VisualElement", null, "a")).Get(UssPropertyId.Width).A);
            Assert.AreEqual(0, cascade.Diagnostics.Count);
        }

        [Test]
        public void Var_Unresolved_ReportsOnceAcrossManyElements_AndLeavesThePropertyUnset()
        {
            FakeCascade cascade = Cascade(".a {\n  width: var(--missing);\n}");
            FakeUssElement root = new("VisualElement");
            for (int i = 0; i < 10; i++)
            {
                root.Add(new FakeUssElement("VisualElement", null, "a"));
            }

            cascade.ResolveTree(root);

            Assert.AreEqual(1, cascade.Diagnostics.CountOf(RuntimeUiDiagnosticCodes.UssUnresolvedVar));
            Assert.AreEqual(2, cascade.Diagnostics.Items[0].Line);
            Assert.IsFalse(cascade.Resolve(root.Children[0]).IsSet(UssPropertyId.Width));
        }

        [Test]
        public void Var_DefinedTwin_ReportsNothing()
        {
            FakeCascade cascade = Cascade(":root { --size: 8px; }\n.a {\n  width: var(--size);\n}");
            FakeUssElement root = new("VisualElement") { PseudoStates = UssPseudoState.Root };
            FakeUssElement child = root.Add(new FakeUssElement("VisualElement", null, "a"));

            cascade.ResolveTree(root);

            Assert.AreEqual(0, cascade.Diagnostics.Count, string.Join("\n", cascade.Diagnostics.Items));
            Assert.AreEqual(8f, cascade.Resolve(child).Get(UssPropertyId.Width).A);
        }

        [Test]
        public void Var_Cycle_IsReported()
        {
            FakeCascade cascade = Cascade(".a { --x: var(--y); --y: var(--x); width: var(--x); }");

            cascade.Resolve(new FakeUssElement("VisualElement", null, "a"));

            Assert.GreaterOrEqual(cascade.Diagnostics.CountOf(RuntimeUiDiagnosticCodes.UssVarCycle), 1,
                string.Join("\n", cascade.Diagnostics.Items));
        }

        [Test]
        public void Var_InsideAShorthand_ResolvesPerScope()
        {
            FakeCascade cascade = Cascade(
                ".small { --pad: 2px; } .big { --pad: 9px; } .box { padding: var(--pad) 1px; }");
            FakeUssElement small = new("VisualElement", null, "small");
            FakeUssElement big = new("VisualElement", null, "big");
            FakeUssElement first = small.Add(new FakeUssElement("VisualElement", null, "box"));
            FakeUssElement second = big.Add(new FakeUssElement("VisualElement", null, "box"));

            Assert.AreEqual(2f, cascade.Resolve(first).Get(UssPropertyId.PaddingTop).A);
            Assert.AreEqual(9f, cascade.Resolve(second).Get(UssPropertyId.PaddingTop).A);
            Assert.AreEqual(1f, cascade.Resolve(second).Get(UssPropertyId.PaddingLeft).A);
        }

        [Test]
        public void Var_SubstitutedValueThatIsInvalid_IsReportedAsAnInvalidValue()
        {
            FakeCascade cascade = Cascade(":root { --w: tall; } .a { width: var(--w); }");
            FakeUssElement root = new("VisualElement") { PseudoStates = UssPseudoState.Root };
            root.Add(new FakeUssElement("VisualElement", null, "a"));

            cascade.ResolveTree(root);

            Assert.AreEqual(1, cascade.Diagnostics.CountOf(RuntimeUiDiagnosticCodes.UssInvalidValue));
        }

        [Test]
        public void ThemeTokens_ReachEveryRoot_AndAScreenDeclarationShadowsThem()
        {
            RuntimeUiTheme theme = new(string.Empty,
                new Dictionary<string, string> { { "--accent", "rgb(0, 0, 255)" } });
            FakeCascade plain = Cascade(".a { color: var(--accent); }");
            plain.RootScope = theme.Scope;
            FakeCascade shadowing = Cascade(":root { --accent: rgb(255, 0, 0); } .a { color: var(--accent); }");
            shadowing.RootScope = theme.Scope;

            FakeUssElement plainRoot = new("VisualElement") { PseudoStates = UssPseudoState.Root };
            FakeUssElement plainLabel = plainRoot.Add(new FakeUssElement("Label", null, "a"));
            FakeUssElement shadowRoot = new("VisualElement") { PseudoStates = UssPseudoState.Root };
            FakeUssElement shadowLabel = shadowRoot.Add(new FakeUssElement("Label", null, "a"));

            Assert.AreEqual(1f, plain.Resolve(plainLabel).Get(UssPropertyId.Color).C, "The theme token applies.");
            Assert.AreEqual(1f, shadowing.Resolve(shadowLabel).Get(UssPropertyId.Color).A, "The screen's own value wins.");
        }

        [Test]
        public void Theme_SetToken_RaisesChangedAndRebuildsTheScope()
        {
            RuntimeUiTheme theme = new();
            int changes = 0;
            theme.Changed += _ => changes++;
            UssCustomProperties before = theme.Scope;

            Assert.IsTrue(theme.SetToken("--cai-color-accent", "rgb(1, 2, 3)"));
            Assert.IsTrue(theme.SetToken("--cai-color-accent", "rgb(1, 2, 3)"), "Setting the same value is a no-op.");
            Assert.IsFalse(theme.SetToken("accent", "red"), "Token names start with --.");

            Assert.AreEqual(1, changes);
            Assert.AreNotSame(before, theme.Scope);
            Assert.IsTrue(theme.Scope.TryGet("--cai-color-accent", out string value));
            Assert.AreEqual("rgb(1, 2, 3)", value);
        }

        [Test]
        public void DefaultTheme_ParsesWithoutReports_AndItsClassesResolveAgainstItsTokens()
        {
            RuntimeUiTheme theme = new();
            FakeCascade cascade = new();
            cascade.Sheets.Add(theme.BaseSheet);
            cascade.DefaultSheetCount = 1;
            cascade.RootScope = theme.Scope;

            UssResolvedStyle style = cascade.Resolve(new FakeUssElement("Button", null, "cai-btn", "cai-btn-primary"));

            Assert.AreEqual(0, theme.BaseSheet.Diagnostics.Count, string.Join("\n", theme.BaseSheet.Diagnostics));
            Assert.IsTrue(style.IsSet(UssPropertyId.BackgroundColor));
            Assert.AreEqual(4f, style.Get(UssPropertyId.PaddingTop).A);
            Assert.AreEqual(0, cascade.Diagnostics.Count, string.Join("\n", cascade.Diagnostics.Items));
        }

        [Test]
        public void TwoHundredElementScreen_ParsesAndCascades_WithinTheBuildBudget()
        {
            string uxml = RuntimeUiFixtureTexts.TwoHundredElementUxml();
            RuntimeUiTheme theme = new();
            Run(uxml, theme);
            Run(uxml, theme);

            double[] samples = new double[9];
            for (int i = 0; i < samples.Length; i++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                int styled = Run(uxml, theme);
                watch.Stop();
                samples[i] = watch.Elapsed.TotalMilliseconds;
                Assert.AreEqual(201, styled, "200 elements plus the screen root.");
            }

            System.Array.Sort(samples);
            double median = samples[samples.Length / 2];
            TestContext.WriteLine("Runtime UI engine-free parse + cascade of a 200-element screen: median " +
                                  median.ToString("0.000") + " ms (min " + samples[0].ToString("0.000") + ", max " +
                                  samples[samples.Length - 1].ToString("0.000") + ")");
            Assert.Less(median, 10.0, "The engine-free half alone must leave room in the 10 ms build budget.");
        }

        private static int Run(string uxml, RuntimeUiTheme theme)
        {
            UxmlDocument document = UxmlParser.Parse(uxml);
            UssStyleSheet sheet = UssStyleSheet.Parse(RuntimeUiFixtureTexts.HotbarUss);
            FakeCascade cascade = new();
            cascade.Sheets.Add(theme.BaseSheet);
            cascade.Sheets.Add(sheet);
            cascade.DefaultSheetCount = 1;
            cascade.RootScope = theme.Scope;
            FakeUssElement root = FakeUssElement.FromNode(document.Root, true);
            int styled = cascade.ResolveTree(root);
            Assert.AreEqual(0, cascade.Diagnostics.Count, string.Join("\n", cascade.Diagnostics.Items));
            return styled;
        }

        private static FakeCascade Cascade(string uss)
        {
            FakeCascade cascade = new();
            UssStyleSheet sheet = UssStyleSheet.Parse(uss);
            Assert.AreEqual(0, sheet.Diagnostics.Count, string.Join("\n", sheet.Diagnostics));
            cascade.Sheets.Add(sheet);
            return cascade;
        }

        private static FakeUssElement WithState(string type, UssPseudoState state, params string[] classes)
        {
            return new FakeUssElement(type, null, classes) { PseudoStates = state };
        }
    }
}
