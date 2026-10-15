using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using CoreAI.Benchmarking;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Engine-free tests for the extreme tier and the redesigned benchmark charts: the extreme/regular
    /// sub-scores and per-group splits (repetition averaging and Environment/Framework/NotGraded exclusions
    /// exactly like the suite score), the JSON fields and the core parser for new and old reports, the
    /// comparison ranking, and well-formed SVG for every card and chart.
    /// </summary>
    public sealed class BenchmarkExtremeTierEditModeTests
    {
        private static ScenarioResult Result(string id, string group, double baseScore, bool extreme = false,
            int repetition = 1, FailureAttribution attribution = FailureAttribution.None,
            string modelId = "test/model")
        {
            List<BenchmarkCheckpoint> cps = new()
            {
                new BenchmarkCheckpoint("hit", "hit", baseScore, true, false, null, BenchmarkDimension.TaskCompletion),
                new BenchmarkCheckpoint("miss", "miss", 100 - baseScore, false, false, null,
                    BenchmarkDimension.Reasoning)
            };
            return new ScenarioResult
            {
                ScenarioId = id,
                ScenarioName = id + " name",
                Group = group,
                Extreme = extreme,
                ModelId = modelId,
                Score = GoalScore.Compute(cps),
                Checkpoints = cps,
                Repetition = repetition,
                Attribution = attribution,
                PromptTokens = 100,
                CompletionTokens = 10,
                GenerationMs = 1000,
                LatencyMs = 2000,
                ToolCalls = 2
            };
        }

        private static BenchmarkReport Report(string modelId, params ScenarioResult[] results)
        {
            BenchmarkReport report = new()
            {
                Metadata = new BenchmarkRunMetadata
                {
                    RunId = "20261015_120000", TimestampUtc = "2026-10-15T12:00:00Z", ModelId = modelId,
                    Backend = "Test", Repetitions = 2, SuiteVersion = "2.0"
                }
            };
            foreach (ScenarioResult r in results)
            {
                r.ModelId = modelId;
                report.Add(r);
            }

            return report;
        }

        /// <summary>Regular G1 at 100, extreme G1 run twice (80, 60), extreme G2 at 40, regular G6 at 90.</summary>
        private static BenchmarkReport Mixed(string modelId = "test/model")
        {
            return Report(modelId,
                Result("g1_regular", "G1", 100),
                Result("g1_extreme", "G1", 80, true, 1),
                Result("g1_extreme", "G1", 60, true, 2),
                Result("g2_extreme", "G2", 40, true),
                Result("g6_build", "G6", 90));
        }

        [Test]
        public void ExtremeScore_AveragesRepetitionsPerScenario_ThenScenarios()
        {
            BenchmarkReport report = Mixed();

            Assert.IsTrue(report.HasExtreme);
            Assert.AreEqual(2, report.ExtremeScenarioCount);
            Assert.AreEqual((70 + 40) / 2.0, report.ExtremeBaseScore, 1e-9);
            Assert.AreEqual(0, report.ExtremePassCount);
            Assert.AreEqual(2, report.RegularScenarioCount);
            Assert.AreEqual(95, report.RegularBaseScore, 1e-9);
            Assert.AreEqual(4, report.ScenarioCount);
            Assert.AreEqual((100 + 70 + 40 + 90) / 4.0, report.SuiteBaseScore, 1e-9,
                "the suite score keeps covering both tiers");
        }

        [Test]
        public void ExtremePass_UsesTheScenarioMean()
        {
            BenchmarkReport report = Report("m",
                Result("g3_extreme", "G3", 95, true, 1),
                Result("g3_extreme", "G3", 85, true, 2));

            Assert.AreEqual(90, report.ExtremeBaseScore, 1e-9);
            Assert.AreEqual(1, report.ExtremePassCount, "mean 90 reaches the PASS bar");
        }

        [TestCase(FailureAttribution.Environment)]
        [TestCase(FailureAttribution.Framework)]
        [TestCase(FailureAttribution.NotGraded)]
        public void ExcludedAttributions_NeverCountTowardTheExtremeTier(FailureAttribution attribution)
        {
            BenchmarkReport report = Report("m",
                Result("g1_regular", "G1", 100),
                Result("g1_extreme", "G1", 0, true, 1, attribution),
                Result("g2_extreme", "G2", 50, true));

            Assert.AreEqual(1, report.ExtremeScenarioCount);
            Assert.AreEqual(50, report.ExtremeBaseScore, 1e-9);

            BenchmarkReport onlyExcluded = Report("m",
                Result("g1_regular", "G1", 100),
                Result("g1_extreme", "G1", 0, true, 1, attribution));
            Assert.IsFalse(onlyExcluded.HasExtreme);
            Assert.AreEqual(0, onlyExcluded.ExtremeBaseScore, 1e-9);
            Assert.AreEqual(100, onlyExcluded.SuiteBaseScore, 1e-9);
        }

        [Test]
        public void GroupBreakdown_SplitsEachGroupIntoTiers()
        {
            Dictionary<string, GroupScore> groups = Mixed().GroupBreakdown().ToDictionary(g => g.Group);

            GroupScore g1 = groups["G1"];
            Assert.AreEqual(85, g1.MeanBase, 1e-9, "group mean covers both tiers");
            Assert.AreEqual(2, g1.Count);
            Assert.AreEqual(1, g1.RegularCount);
            Assert.AreEqual(100, g1.RegularMeanBase, 1e-9);
            Assert.IsTrue(g1.HasExtreme);
            Assert.AreEqual(70, g1.ExtremeMeanBase, 1e-9);
            Assert.AreEqual(0, g1.ExtremePassCount);

            Assert.IsFalse(groups["G6"].HasExtreme);
            Assert.AreEqual(90, groups["G6"].MeanBase, 1e-9);
            Assert.AreEqual(0, groups["G2"].RegularCount);
            Assert.AreEqual(40, groups["G2"].ExtremeMeanBase, 1e-9);
        }

        [Test]
        public void Json_CarriesTierFields_AndRoundTripsThroughTheCoreParser()
        {
            BenchmarkReport report = Mixed();
            string json = BenchmarkReportFormatter.ToJson(report);

            StringAssert.Contains("\"extreme\":true", json);
            StringAssert.Contains("\"extremeBaseScore\":55", json);
            StringAssert.Contains("\"groups\":{\"G1\":{\"meanBase\":85", json);
            StringAssert.Contains("\"suiteBaseScore\":", json, "existing fields stay");

            ModelSummary s = ModelComparison.ParseSummaryJson(json);
            Assert.IsNotNull(s);
            Assert.AreEqual("test/model", s.ModelId);
            Assert.AreEqual("2.0", s.SuiteVersion);
            Assert.AreEqual(report.SuiteBaseScore, s.SuiteBase, 1e-6);
            Assert.AreEqual(report.RegularBaseScore, s.RegularBase, 1e-6);
            Assert.AreEqual(report.ExtremeBaseScore, s.ExtremeBase, 1e-6);
            Assert.AreEqual(2, s.ExtremeScenarios);
            Assert.AreEqual(0, s.ExtremePass);
            Assert.AreEqual(4, s.Scenarios);
            Assert.AreEqual(85, s.Groups["G1"], 1e-6);
            Assert.AreEqual(70, s.ExtremeGroups["G1"], 1e-6);
            Assert.IsFalse(s.ExtremeGroups.ContainsKey("G6"));
            Assert.AreEqual(report.TotalTokens, s.TotalTokens);

            ModelSummary direct = ModelSummary.From(report);
            Assert.AreEqual(direct.ExtremeBase, s.ExtremeBase, 1e-6);
            CollectionAssert.AreEquivalent(direct.Groups.Keys, s.Groups.Keys);
        }

        [Test]
        public void Parser_WithoutSummaryTierFields_RecomputesThemFromResults()
        {
            string json = BenchmarkReportFormatter.ToJson(Mixed());
            string stripped = Regex.Replace(json, "\"groups\":\\{(\"[^\"]*\":\\{[^}]*\\},?)*\\},", "");
            foreach (string key in new[]
                     {
                         "scenarios", "regularBaseScore", "regularScenarios", "extremeBaseScore", "extremeScenarios",
                         "extremePass"
                     })
            {
                stripped = Regex.Replace(stripped, $"\"{key}\":[0-9.eE+-]+,", "");
            }

            StringAssert.DoesNotContain("extremeBaseScore", stripped);
            StringAssert.DoesNotContain("\"groups\"", stripped);

            ModelSummary fromSummary = ModelComparison.ParseSummaryJson(json);
            ModelSummary fromResults = ModelComparison.ParseSummaryJson(stripped);
            Assert.IsNotNull(fromResults);
            Assert.AreEqual(fromSummary.ExtremeBase, fromResults.ExtremeBase, 1e-6);
            Assert.AreEqual(fromSummary.RegularBase, fromResults.RegularBase, 1e-6);
            Assert.AreEqual(fromSummary.ExtremeScenarios, fromResults.ExtremeScenarios);
            Assert.AreEqual(fromSummary.ExtremePass, fromResults.ExtremePass);
            Assert.AreEqual(fromSummary.Scenarios, fromResults.Scenarios);
            CollectionAssert.AreEquivalent(fromSummary.Groups, fromResults.Groups);
            CollectionAssert.AreEquivalent(fromSummary.ExtremeGroups, fromResults.ExtremeGroups);
        }

        [Test]
        public void Parser_ReadsAPreTierReport_WithoutAnExtremeTier()
        {
            const string oldJson = "{\"metadata\":{\"runId\":\"20260929_170723\",\"timestampUtc\":\"2026-09-29T12:07:23Z\"," +
                                   "\"modelId\":\"codex/GPT-6 Sol\",\"repetitions\":1,\"suiteVersion\":\"1.15\"}," +
                                   "\"summary\":{\"suiteBaseScore\":95,\"passRate\":0.5,\"pass\":1,\"partial\":1," +
                                   "\"fail\":0,\"totalTokens\":15041,\"totalCompletionTokens\":2162," +
                                   "\"totalGenerationMs\":1824633.2,\"totalLatencyMs\":1824752.3," +
                                   "\"totalToolCalls\":55,\"failedToolCalls\":11," +
                                   "\"dimensions\":{\"ToolCorrectness\":100,\"Reasoning\":90}," +
                                   "\"gameFitOverall\":9.8,\"bestRole\":\"Scene / Tool Operator\"," +
                                   "\"roles\":{\"Scene / Tool Operator\":10}}," +
                                   "\"results\":[" +
                                   "{\"scenarioId\":\"g1_a\",\"group\":\"G1\",\"repetition\":1,\"attribution\":\"None\",\"base\":100}," +
                                   "{\"scenarioId\":\"g2_a\",\"group\":\"G2\",\"repetition\":1,\"attribution\":\"None\",\"base\":80}," +
                                   "{\"scenarioId\":\"g2_a\",\"group\":\"G2\",\"repetition\":2,\"attribution\":\"None\",\"base\":100}," +
                                   "{\"scenarioId\":\"g6_b\",\"group\":\"G6\",\"repetition\":1,\"attribution\":\"Environment\",\"base\":0}]}";

            ModelSummary s = ModelComparison.ParseSummaryJson(oldJson);

            Assert.IsNotNull(s);
            Assert.AreEqual("codex/GPT-6 Sol", s.ModelId);
            Assert.AreEqual("1.15", s.SuiteVersion);
            Assert.AreEqual(95, s.SuiteBase, 1e-9);
            Assert.IsFalse(s.HasExtreme);
            Assert.AreEqual(0, s.ExtremeGroups.Count);
            Assert.AreEqual(2, s.Scenarios, "environment failures are excluded like the suite score");
            Assert.AreEqual(2, s.RegularScenarios);
            Assert.AreEqual(95, s.RegularBase, 1e-9);
            Assert.AreEqual(100, s.Groups["G1"], 1e-9);
            Assert.AreEqual(90, s.Groups["G2"], 1e-9, "repetitions average per scenario");
            Assert.IsFalse(s.Groups.ContainsKey("G6"));
            Assert.AreEqual(0.2, s.ToolErrorRate, 1e-9);
            Assert.AreEqual(10, s.Roles["Scene / Tool Operator"], 1e-9);
            Assert.AreEqual(90, s.Dimensions["Reasoning"], 1e-9);
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("[1,2,3]")]
        [TestCase("{}")]
        [TestCase("{\"metadata\":{\"modelId\":\"m\"}}")]
        [TestCase("{\"metadata\":{\"modelId\":\"m\"},\"summary\":{\"suiteBaseScore\":1")]
        public void Parser_ReturnsNull_ForAnythingButAReport(string json)
        {
            Assert.IsNull(ModelComparison.ParseSummaryJson(json));
        }

        [Test]
        public void Parser_DecodesEscapes()
        {
            ModelSummary s = ModelComparison.ParseSummaryJson(
                "{\"metadata\":{\"modelId\":\"caf\\u00e9 \\\"x\\\"\\n\"},\"summary\":{\"suiteBaseScore\":-1.5e1}}");

            Assert.AreEqual("café \"x\"\n", s.ModelId);
            Assert.AreEqual(-15, s.SuiteBase, 1e-9);
        }

        [Test]
        public void Markdown_ShowsTierScores_GroupTable_AndExtremeSection()
        {
            string md = BenchmarkReportFormatter.ToMarkdown(Mixed());

            StringAssert.Contains("regular tier 95/100 (2 scenarios)", md);
            StringAssert.Contains("extreme tier **55**/100 (0/2 pass)", md);
            StringAssert.Contains("## 🧩 Scores by group", md);
            StringAssert.Contains("| G1 | Build | 2 | 85 | 1/2 | 70 · 🟡 PARTIAL |", md);
            StringAssert.Contains("| G6 | Free build | 1 | 90 | 1/1 | — |", md);
            StringAssert.Contains("## 🔥 Extreme tier — 55/100 (0/2 pass)", md);
            StringAssert.Contains("| G2 | g2_extreme name | 40 | ❌ FAIL | 1 | 0 |", md);

            string plain = BenchmarkReportFormatter.ToMarkdown(Report("m", Result("g1", "G1", 100)));
            StringAssert.Contains("extreme tier n/a", plain);
            StringAssert.Contains("_No extreme-tier scenario was graded in this run._", plain);
        }

        [Test]
        public void Comparison_RanksBySuite_BreaksTiesByExtreme_AndKeepsPinnedFirst()
        {
            ModelSummary a = ModelSummary.From(Report("a/low-extreme", Result("g1", "G1", 100),
                Result("g1x", "G1", 60, true)));
            ModelSummary b = ModelSummary.From(Report("b/high-extreme", Result("g1", "G1", 100),
                Result("g1x", "G1", 60, true)));
            b.ExtremeBase = 80;
            ModelSummary c = ModelSummary.From(Report("c/top", Result("g1", "G1", 100), Result("g1x", "G1", 100, true)));
            List<ModelSummary> models = new() { a, b, c };

            string md = ModelComparison.Build(models);
            int ic = md.IndexOf("`c/top`", System.StringComparison.Ordinal);
            int ib = md.IndexOf("`b/high-extreme`", System.StringComparison.Ordinal);
            int ia = md.IndexOf("`a/low-extreme`", System.StringComparison.Ordinal);
            Assert.That(ic < ib && ib < ia, "suite first, then the extreme score breaks the tie");
            StringAssert.Contains("## Scores by group", md);
            StringAssert.Contains("## Extreme tier ranking", md);

            string pinned = ModelComparison.Build(models, "t", "a/low-extreme");
            Assert.That(pinned.IndexOf("`a/low-extreme`", System.StringComparison.Ordinal)
                        < pinned.IndexOf("`c/top`", System.StringComparison.Ordinal));
        }

        [TestCase(100, 5)]
        [TestCase(99.94, 4)]
        [TestCase(90, 4)]
        [TestCase(89.99, 3)]
        [TestCase(75, 3)]
        [TestCase(74.9, 2)]
        [TestCase(50, 2)]
        [TestCase(49.9, 1)]
        [TestCase(double.NaN, 1)]
        public void HeatBins_FollowTheVerdictThresholds(double score, int bin)
        {
            Assert.AreEqual(bin, BenchmarkCardScene.HeatBin(score));
        }

        [Test]
        public void EveryCardAndChart_IsWellFormedSvg_EvenWithHostileModelIds()
        {
            BenchmarkReport hostile = Mixed("evil <model> & \"quotes\" \u0001");
            BenchmarkReport empty = Report("empty/model");
            List<ModelSummary> summaries = new()
            {
                ModelSummary.From(hostile), ModelSummary.From(Mixed("second")), ModelSummary.From(empty)
            };

            List<string> svgs = new()
            {
                BenchmarkReportFormatter.ToSvg(hostile),
                BenchmarkReportFormatter.ToSvg(empty),
                BenchmarkReportFormatter.ToModelCardSvg(hostile),
                BenchmarkReportFormatter.ToModelCardSvg(hostile, CardTheme.Light),
                BenchmarkReportFormatter.ToModelCardSvg(empty, CardTheme.Dark),
                ModelComparison.ToComparisonSvg(summaries),
                ModelComparison.ToComparisonSvg(summaries, "second"),
                ModelComparison.ToComparisonSvg(new List<ModelSummary>()),
                ModelComparison.ToGroupHeatmapSvg(summaries),
                ModelComparison.ToGroupHeatmapSvg(new List<ModelSummary>()),
                ModelComparison.ToExtremeRankingSvg(summaries, "second"),
                ModelComparison.ToExtremeRankingSvg(new List<ModelSummary> { ModelSummary.From(empty) })
            };

            foreach (string svg in svgs)
            {
                XmlDocument doc = new();
                Assert.DoesNotThrow(() => doc.LoadXml(svg), svg.Substring(0, System.Math.Min(svg.Length, 200)));
                Assert.AreEqual("svg", doc.DocumentElement.Name);
                Assert.That(double.Parse(doc.DocumentElement.GetAttribute("height"),
                    System.Globalization.CultureInfo.InvariantCulture), Is.GreaterThan(0));
            }

            string inlineMd = BenchmarkReportFormatter.ToMarkdown(hostile);
            int start = inlineMd.IndexOf("<svg", System.StringComparison.Ordinal);
            int end = inlineMd.IndexOf("</svg>", start, System.StringComparison.Ordinal) + "</svg>".Length;
            Assert.DoesNotThrow(() => new XmlDocument().LoadXml(inlineMd.Substring(start, end - start)));
        }

        [Test]
        public void ModelCard_KeepsEveryPrimitiveInsideTheCanvas()
        {
            BenchmarkCardScene scene = BenchmarkReportFormatter.ModelCardScene(
                Mixed("a-very-long-provider/model-name-that-keeps-going-heretic-imatrix-max-q4_k_m-experimental-build"));

            Assert.AreEqual(1600, scene.Width);
            Assert.AreEqual(900, scene.Height);
            foreach (CardShape s in scene.Shapes)
            {
                if (s.Kind == CardShapeKind.Text)
                {
                    double w = BenchmarkCardScene.TextWidth(s.Text, s.Size, s.Bold) +
                               (string.IsNullOrEmpty(s.Suffix)
                                   ? 0
                                   : s.SuffixGap + BenchmarkCardScene.TextWidth(s.Suffix, s.SuffixSize, s.SuffixBold));
                    double left = s.Anchor == CardAnchor.Start ? s.X : s.Anchor == CardAnchor.Middle ? s.X - w / 2 : s.X - w;
                    Assert.That(left, Is.GreaterThanOrEqualTo(-0.5), s.Text);
                    Assert.That(left + w, Is.LessThanOrEqualTo(scene.Width + 0.5), s.Text);
                    Assert.That(s.Y, Is.InRange(s.Size * 0.7, scene.Height), s.Text);
                }
                else if (s.Kind == CardShapeKind.Rect)
                {
                    Assert.That(s.X, Is.GreaterThanOrEqualTo(0));
                    Assert.That(s.X + s.W, Is.LessThanOrEqualTo(scene.Width));
                    Assert.That(s.Y + s.H, Is.LessThanOrEqualTo(scene.Height));
                }
            }
        }
    }
}
