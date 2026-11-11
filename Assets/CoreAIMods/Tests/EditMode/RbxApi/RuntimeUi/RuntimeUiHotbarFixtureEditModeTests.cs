using System.Collections.Generic;
using System.Text.RegularExpressions;
using CoreAI.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.RuntimeUi
{
    /// <summary>
    /// The skill's hotbar example must be something the interpreter accepts as written: its UXML and USS
    /// parse with no report, and every design token it reads exists in the default theme. A report here
    /// means the AI is being taught markup that silently loses a rule.
    /// </summary>
    [TestFixture]
    public sealed class RuntimeUiHotbarFixtureEditModeTests
    {
        [Test]
        public void P18_HotbarExample_UxmlParsesWithoutAReport_AndBuildsEightElements()
        {
            UxmlDocument document = UxmlParser.Parse(RuntimeUiHotbarFixture.Uxml);

            Assert.AreEqual(0, document.Diagnostics.Count, Describe(document.Diagnostics));
            Assert.AreEqual(8, document.ElementCount);
        }

        [Test]
        public void P18_HotbarExample_UssParsesWithoutAReport()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(RuntimeUiHotbarFixture.Uss);

            Assert.AreEqual(0, sheet.Diagnostics.Count, Describe(sheet.Diagnostics));
        }

        [Test]
        public void P18_HotbarExample_ReadsOnlyTokensTheDefaultThemeDefines()
        {
            RuntimeUiTheme theme = new();
            List<string> missing = new();
            foreach (Match match in Regex.Matches(RuntimeUiHotbarFixture.Uss, @"var\((--[a-z0-9-]+)"))
            {
                if (!theme.TryGetToken(match.Groups[1].Value, out _))
                {
                    missing.Add(match.Groups[1].Value);
                }
            }

            CollectionAssert.IsEmpty(missing);
        }

        [Test]
        public void P18_AStyleSheetWithAnUnsupportedProperty_IsReported_SoTheCheckAboveCanFail()
        {
            UssStyleSheet sheet = UssStyleSheet.Parse(".slot { gap: 4px; }");

            Assert.AreEqual(1, sheet.Diagnostics.Count);
        }

        private static string Describe(IReadOnlyList<RuntimeUiDiagnostic> diagnostics)
        {
            string text = string.Empty;
            for (int i = 0; i < diagnostics.Count; i++)
            {
                text += diagnostics[i] + "\n";
            }

            return text;
        }
    }
}
