using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using CoreAI.Benchmarking;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Engine-free tests that keep benchmark reports truthful about dead provider routes and partial runs: a run
    /// with no graded scenario renders unrated everywhere (and its twin, a graded run that scored zero, keeps its
    /// score), comparisons list unrated runs unranked and expose a like-for-like mean over the groups every
    /// compared model graded, and a target's display name names its reports while the short label stays the
    /// operator's handle.
    /// </summary>
    public sealed class BenchmarkReportingHonestyEditModeTests
    {
        private static ScenarioResult Result(string id, string group, double baseScore,
            FailureAttribution attribution = FailureAttribution.None, bool extreme = false)
        {
            List<BenchmarkCheckpoint> checkpoints = new()
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
                Score = GoalScore.Compute(checkpoints),
                Checkpoints = checkpoints,
                Repetition = 1,
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
                    RunId = "20261017_013724", TimestampUtc = "2026-10-17T01:37:24Z", ModelId = modelId,
                    Backend = "Test", Repetitions = 1, SuiteVersion = "2.0"
                }
            };
            foreach (ScenarioResult result in results)
            {
                result.ModelId = modelId;
                report.Add(result);
            }

            return report;
        }

        /// <summary>A dead provider route: every scenario failed in transport and is attributed to Environment.</summary>
        private static BenchmarkReport DeadRoute(string modelId = "LL Alpha Free")
        {
            return Report(modelId,
                Result("g1_a", "G1", 0, FailureAttribution.Environment),
                Result("g2_a", "G2", 0, FailureAttribution.Environment),
                Result("g6_free_build", "G6", 0, FailureAttribution.Environment));
        }

        /// <summary>The twin: the model answered and every scenario was graded, scoring zero.</summary>
        private static BenchmarkReport ScoredZero(string modelId = "zero/model")
        {
            return Report(modelId,
                Result("g1_a", "G1", 0, FailureAttribution.Model),
                Result("g2_a", "G2", 0, FailureAttribution.Model));
        }

        private static List<string> Texts(BenchmarkCardScene scene)
        {
            return scene.Shapes.Where(s => s.Kind == CardShapeKind.Text).Select(s => s.Text).ToList();
        }

        private static bool HasScoredSuiteTile(BenchmarkCardScene scene)
        {
            return scene.Shapes.Any(s => s.Kind == CardShapeKind.Text && s.Suffix == "/100");
        }

        [Test]
        public void AllEnvironmentRun_HasNoGradedScenarios()
        {
            BenchmarkReport dead = DeadRoute();

            Assert.IsFalse(dead.HasGradedScenarios);
            Assert.AreEqual(0, dead.ScenarioCount);
            Assert.AreEqual(3, dead.UngradedResults);
            Assert.IsTrue(ScoredZero().HasGradedScenarios, "a graded run that scored zero is still graded");
        }

        [Test]
        public void AllEnvironmentRun_ModelCard_SaysNoGradedScenarios_WithUnratedTiles()
        {
            BenchmarkCardScene card = BenchmarkReportFormatter.ModelCardScene(DeadRoute());
            List<string> texts = Texts(card);

            Assert.IsFalse(HasScoredSuiteTile(card), "no tile may print a score out of 100");
            CollectionAssert.Contains(texts, BenchmarkReportFormatter.NoGradedScenarios);
            CollectionAssert.DoesNotContain(texts, "Failing · regular tier 0.0");
            Assert.IsFalse(texts.Any(t => t.StartsWith("Failing", StringComparison.Ordinal)));
            CollectionAssert.DoesNotContain(texts, "0/0", "the verdict tile must not read 0/0 pass");
            CollectionAssert.Contains(texts, "0 of 3 results graded");
            Assert.AreEqual(3, texts.Count(t => t == "not graded"), "G1, G2 and G6 ran but were not graded");
            Assert.AreEqual(5, texts.Count(t => t == "not run"));
            Assert.IsTrue(texts.Any(t => t.StartsWith("No model measurement: 3 result(s), none graded: 3 environment",
                StringComparison.Ordinal)), "the footer says why the card is empty");
            StringAssert.StartsWith("No graded scenarios", card.Description);
        }

        [Test]
        public void AllEnvironmentRun_ResultsCard_MarkdownAndIndex_AreUnrated()
        {
            BenchmarkReport dead = DeadRoute();

            BenchmarkCardScene results = BenchmarkReportFormatter.ResultsCardScene(dead);
            Assert.IsFalse(HasScoredSuiteTile(results));
            CollectionAssert.Contains(Texts(results), BenchmarkReportFormatter.NoGradedScenarios);

            string md = BenchmarkReportFormatter.ToMarkdown(dead);
            StringAssert.StartsWith("# 🎮 LL Alpha Free — no graded scenarios", md);
            StringAssert.Contains("> **No graded scenarios** · 3 result(s), none graded: 3 environment", md);
            StringAssert.Contains("- **Scores:** unrated", md);
            StringAssert.Contains("## 🎯 Game-fitness — n/a (no graded scenarios)", md);
            StringAssert.DoesNotContain("Failing", md);
            StringAssert.DoesNotContain("0/100", md);

            string row = BenchmarkReportFormatter.IndexRow(dead, "BENCHMARK_x.md");
            StringAssert.Contains("| n/a (no graded scenarios) |", row);
            StringAssert.DoesNotContain("**0**", row);

            foreach (string svg in new[]
                     {
                         BenchmarkReportFormatter.ToSvg(dead), BenchmarkReportFormatter.ToModelCardSvg(dead),
                         BenchmarkReportFormatter.ToModelCardSvg(dead, CardTheme.Dark)
                     })
            {
                Assert.DoesNotThrow(() => new XmlDocument().LoadXml(svg));
            }
        }

        [Test]
        public void GradedRunScoringZero_StillRendersItsScore()
        {
            BenchmarkReport zero = ScoredZero();

            BenchmarkCardScene card = BenchmarkReportFormatter.ModelCardScene(zero);
            List<string> texts = Texts(card);
            Assert.IsTrue(card.Shapes.Any(s => s.Kind == CardShapeKind.Text && s.Text == "0.0" && s.Suffix == "/100"),
                "the zero is a measured score and is printed as one");
            Assert.IsTrue(texts.Any(t => t.StartsWith("Failing", StringComparison.Ordinal)));
            CollectionAssert.Contains(texts, "0/2");
            CollectionAssert.DoesNotContain(texts, BenchmarkReportFormatter.NoGradedScenarios);

            string md = BenchmarkReportFormatter.ToMarkdown(zero);
            StringAssert.StartsWith("# 🎮 zero/model — 0/100", md);
            StringAssert.Contains("> **Failing**", md);
            StringAssert.DoesNotContain("no graded scenarios", md);
            StringAssert.Contains("| **0** |", BenchmarkReportFormatter.IndexRow(zero, "r.md"));
        }

        [Test]
        public void UngradedRun_NamesEveryCauseOfExclusion()
        {
            ScenarioResult excluded = Result("g5_custom", "G5", 100);
            excluded.ExcludeFromScoring = true;
            BenchmarkReport report = Report("mixed/model",
                Result("g1_a", "G1", 0, FailureAttribution.Environment),
                Result("g2_a", "G2", 0, FailureAttribution.Framework),
                Result("g3_a", "G3", 50, FailureAttribution.NotGraded),
                excluded);

            Assert.IsFalse(report.HasGradedScenarios);
            StringAssert.Contains("4 result(s), none graded: 1 environment, 1 framework, 2 not graded",
                BenchmarkReportFormatter.ToMarkdown(report));
        }

        [Test]
        public void PartialRun_GroupWithoutAGradedScenario_IsNotGradedRatherThanNotRun()
        {
            BenchmarkReport partial = Report("partial/model",
                Result("g1_a", "G1", 100),
                Result("g6_free_build", "G6", 0, FailureAttribution.Environment));

            List<string> texts = Texts(BenchmarkReportFormatter.ModelCardScene(partial));

            Assert.IsTrue(partial.HasGradedScenarios);
            Assert.AreEqual(1, texts.Count(t => t == "not graded"), "G6 ran and was not graded");
            Assert.AreEqual(6, texts.Count(t => t == "not run"));
        }

        [Test]
        public void Comparison_ListsADeadRouteUnrankedAfterARealZero()
        {
            ModelSummary scored = ModelSummary.From(Report("scored/model", Result("g1_a", "G1", 40),
                Result("g2_a", "G2", 60)));
            ModelSummary dead = ModelComparison.ParseSummaryJson(BenchmarkReportFormatter.ToJson(DeadRoute()));
            ModelSummary zero = ModelSummary.From(ScoredZero());
            List<ModelSummary> models = new() { dead, zero, scored };

            Assert.IsNotNull(dead);
            Assert.IsFalse(dead.HasGradedScenarios, "the JSON round trip keeps the run unrated");
            Assert.IsTrue(zero.HasGradedScenarios);

            string md = ModelComparison.Build(models);
            int scoredAt = md.IndexOf("`scored/model`", StringComparison.Ordinal);
            int zeroAt = md.IndexOf("`zero/model`", StringComparison.Ordinal);
            int deadAt = md.IndexOf("`LL Alpha Free`", StringComparison.Ordinal);
            Assert.That(scoredAt < zeroAt && zeroAt < deadAt, "a dead route never ranks among real scores");
            StringAssert.Contains("| – | `LL Alpha Free` | n/a (no graded scenarios) |", md);
            StringAssert.Contains("| 2 | `zero/model` | **0** |", md);
            StringAssert.Contains("1 model(s) have no graded scenarios", md);
            string mermaid = md.Substring(md.IndexOf("```mermaid", StringComparison.Ordinal));
            StringAssert.DoesNotContain("LL Alpha Free", mermaid.Substring(0, mermaid.IndexOf("```", 3, StringComparison.Ordinal)));

            BenchmarkCardScene chart = ModelComparison.ComparisonScene(models);
            CollectionAssert.Contains(Texts(chart), "unrated: no graded scenarios");
            Assert.DoesNotThrow(() => new XmlDocument().LoadXml(ModelComparison.ToComparisonSvg(models)));
        }

        [Test]
        public void Comparison_OfOnlyDeadRoutes_HasNoRankAndNoChartBars()
        {
            List<ModelSummary> models = new()
            {
                ModelSummary.From(DeadRoute("dead/a")), ModelSummary.From(DeadRoute("dead/b"))
            };

            string md = ModelComparison.Build(models);

            StringAssert.Contains("| – | `dead/a` |", md);
            StringAssert.Contains("| – | `dead/b` |", md);
            StringAssert.Contains("_No model to chart", md);
            StringAssert.DoesNotContain("Like-for-like", md, "no graded model, nothing to compare like for like");
        }

        [Test]
        public void LikeForLike_UsesOnlyTheGroupsEveryComparedModelGraded()
        {
            ModelSummary full = ModelSummary.From(Report("full/model",
                Result("g1_a", "G1", 100), Result("g2_a", "G2", 50), Result("g6_free_build", "G6", 90)));
            ModelSummary partial = ModelSummary.From(Report("partial/model",
                Result("g1_a", "G1", 80), Result("g2_a", "G2", 60),
                Result("g6_free_build", "G6", 0, FailureAttribution.Environment)));
            ModelSummary dead = ModelSummary.From(DeadRoute());
            List<ModelSummary> models = new() { full, partial, dead };

            IReadOnlyList<string> shared = ModelComparison.SharedGroups(models);

            CollectionAssert.AreEqual(new[] { "G1", "G2" }, shared, "G6 is missing from one model; the dead route takes no part");
            Assert.AreEqual(75, ModelComparison.LikeForLikeMean(full, shared).Value, 1e-9);
            Assert.AreEqual(70, ModelComparison.LikeForLikeMean(partial, shared).Value, 1e-9);
            Assert.IsNull(ModelComparison.LikeForLikeMean(dead, shared));

            string md = ModelComparison.Build(models);
            StringAssert.Contains("Like-for-like = the mean of the group scores over G1, G2, the groups every " +
                                  "compared model graded.", md);
            StringAssert.Contains("| # | Model | Suite | Like-for-like | Extreme |", md);
            StringAssert.Contains("| 1 | `full/model` | **80** | 75 |", md);
            StringAssert.Contains("| 2 | `partial/model` | **70** | 70 |", md);
            CollectionAssert.Contains(Texts(ModelComparison.ComparisonScene(models)),
                "Like-for-like = mean of the group scores over G1, G2, the groups every model graded.");
        }

        [Test]
        public void LikeForLike_OfModelsThatRanTheSameGroups_IsTheMeanOfTheirGroupScores()
        {
            ModelSummary a = ModelSummary.From(Report("a/model",
                Result("g1_a", "G1", 100), Result("g1_b", "G1", 80), Result("g2_a", "G2", 60)));
            ModelSummary b = ModelSummary.From(Report("b/model", Result("g1_a", "G1", 50), Result("g2_a", "G2", 70)));

            IReadOnlyList<string> shared = ModelComparison.SharedGroups(new List<ModelSummary> { a, b });

            CollectionAssert.AreEqual(new[] { "G1", "G2" }, shared);
            Assert.AreEqual((90 + 60) / 2.0, ModelComparison.LikeForLikeMean(a, shared).Value, 1e-9,
                "each group weighs the same, unlike the per-scenario suite score (80)");
            Assert.AreEqual(80, a.SuiteBase, 1e-9);
        }

        [Test]
        public void LikeForLike_IsAbsentForOneModel_AndSaysSoWhenNoGroupIsShared()
        {
            ModelSummary g1 = ModelSummary.From(Report("g1/model", Result("g1_a", "G1", 100)));
            ModelSummary g2 = ModelSummary.From(Report("g2/model", Result("g2_a", "G2", 100)));

            StringAssert.DoesNotContain("Like-for-like", ModelComparison.Build(new List<ModelSummary> { g1 }));

            string md = ModelComparison.Build(new List<ModelSummary> { g1, g2 });
            CollectionAssert.IsEmpty(ModelComparison.SharedGroups(new List<ModelSummary> { g1, g2 }));
            StringAssert.Contains("No group was graded by every compared model", md);
            StringAssert.Contains("| 1 | `g1/model` | **100** | – |", md);
        }

        [Test]
        public void TargetDisplayName_NamesTheReport_WhileTheLabelStaysTheHandle()
        {
            List<BenchmarkTarget> targets = BenchmarkTargetList.Parse(
                "[{\"label\":\"beam\",\"displayName\":\"Beam Free\",\"model\":\"opencode/beam-free\"," +
                "\"baseUrl\":\"http://127.0.0.1:8831/v1\"}," +
                "{\"label\":\"glm\",\"display_name\":\"GLM-5.3-Flash Free\",\"model\":\"opencode/glm-5.3-flash-free\"," +
                "\"baseUrl\":\"http://127.0.0.1:8832/v1\"}]");

            BenchmarkTarget beam = targets[0];
            Assert.AreEqual("beam", beam.Label);
            Assert.AreEqual("beam", beam.Handle, "file names and the OnlyTargets filter keep the short label");
            Assert.AreEqual("Beam Free", beam.ExplicitDisplayName);
            Assert.AreEqual("Beam Free", beam.DisplayName, "the report's model id is the display name, not the label");
            Assert.IsTrue(beam.HasDistinctDisplayName);
            Assert.AreEqual("GLM-5.3-Flash Free", targets[1].DisplayName, "display_name is an alias");
        }

        [Test]
        public void TargetWithoutDisplayName_KeepsTheNameItAlwaysHad()
        {
            List<BenchmarkTarget> targets = BenchmarkTargetList.Parse(
                "beam|opencode/beam-free|http://127.0.0.1:8831/v1; opencode/big-pickle|http://127.0.0.1:8832/v1");

            Assert.AreEqual("", targets[0].ExplicitDisplayName);
            Assert.AreEqual("beam", targets[0].DisplayName, "an existing label-only list names its reports as before");
            Assert.AreEqual("opencode/big-pickle", targets[1].DisplayName);
            Assert.IsFalse(targets[1].HasDistinctDisplayName);
            Assert.AreEqual("[{\"model\":\"opencode/beam-free\",\"baseUrl\":\"http://127.0.0.1:8831/v1\",\"label\":\"beam\"}]",
                BenchmarkTargetList.ToJson(new[] { targets[0] }, false),
                "a list without display names serializes exactly as before");
        }

        [Test]
        public void TargetDisplayName_RoundTripsThroughJson()
        {
            BenchmarkTarget target = new("opencode/beam-free", "http://127.0.0.1:8831/v1", "", "beam",
                displayName: "Beam Free");

            string json = BenchmarkTargetList.ToJson(new[] { target }, false);
            BenchmarkTarget back = BenchmarkTargetList.Parse(json)[0];

            StringAssert.Contains("\"displayName\":\"Beam Free\"", json);
            Assert.AreEqual("Beam Free", back.DisplayName);
            Assert.AreEqual("beam", back.Handle);
        }

        [Test]
        public void TargetDisplayName_MustBeAString()
        {
            Assert.IsFalse(BenchmarkTargetList.TryParse(
                "[{\"displayName\":7,\"model\":\"m\",\"baseUrl\":\"http://h:1/v1\"}]", out _, out string error));
            StringAssert.Contains("'displayName' must be a string", error);
        }

        [Test]
        public void DuplicateDisplayNames_AreNumbered_WithoutRenamingDistinctLabels()
        {
            List<BenchmarkTarget> targets = BenchmarkTargetList.Parse(
                "[{\"label\":\"a\",\"displayName\":\"Same\",\"model\":\"m1\",\"baseUrl\":\"http://h:1/v1\"}," +
                "{\"label\":\"b\",\"displayName\":\"Same\",\"model\":\"m2\",\"baseUrl\":\"http://h:2/v1\"}," +
                "{\"displayName\":\"c\",\"model\":\"m3\",\"baseUrl\":\"http://h:3/v1\"}," +
                "{\"label\":\"c\",\"model\":\"m4\",\"baseUrl\":\"http://h:4/v1\"}]");

            CollectionAssert.AreEqual(new[] { "Same", "Same#2", "c", "c#2" }, targets.Select(t => t.DisplayName).ToArray());
            CollectionAssert.AreEqual(new[] { "a", "b", "m3", "c" }, targets.Select(t => t.Handle).ToArray(),
                "handles were already distinct, so no label changed");
        }

        [Test]
        public void StoredTarget_KeepsTheDisplayName_AndAnOldManifestStillResolves()
        {
            BenchmarkTarget target = new("opencode/beam-free", "http://127.0.0.1:8831/v1", "", "beam",
                displayName: "Beam Free");
            BenchmarkStoredTarget stored = BenchmarkStoredTarget.From(target, "slug");

            Assert.AreEqual("Beam Free", stored.DisplayName);
            Assert.AreEqual("Beam Free", stored.ReportName);
            Assert.AreEqual("Beam Free", stored.Resolve().DisplayName);
            Assert.AreEqual("Beam Free", BenchmarkStoredTarget.Deferred(target, "slug").DisplayName);

            BenchmarkStoredTarget legacy = JsonConvert.DeserializeObject<BenchmarkStoredTarget>(
                "{\"Slug\":\"slug\",\"Model\":\"opencode/beam-free\",\"BaseUrl\":\"http://127.0.0.1:8831/v1\"," +
                "\"Label\":\"beam\"}");
            Assert.AreEqual("", legacy.DisplayName);
            Assert.AreEqual("beam", legacy.ReportName, "a manifest written before display names keeps its report name");
            Assert.AreEqual("beam", legacy.Resolve().DisplayName);
        }

        [Test]
        public void StoredTarget_RejectsAChangedDisplayName_AndChecksLegacyReportsByIt()
        {
            BenchmarkStoredTarget original = BenchmarkStoredTarget.From(
                new BenchmarkTarget("m", "http://h:1/v1", "", "beam", displayName: "Beam Free"), "slug");
            BenchmarkStoredTarget renamed = BenchmarkStoredTarget.From(
                new BenchmarkTarget("m", "http://h:1/v1", "", "beam", displayName: "Other"), "slug");
            BenchmarkStoredTarget same = BenchmarkStoredTarget.From(
                new BenchmarkTarget("m", "http://h:1/v1", "", "beam", displayName: "Beam Free"), "slug");

            Assert.Throws<InvalidOperationException>(() => original.Validate(renamed));
            Assert.DoesNotThrow(() => original.Validate(same));
            Assert.DoesNotThrow(() => original.ValidateLegacyMetadata(
                new BenchmarkRunMetadata { ModelId = "Beam Free" }, true, false, false));
            Assert.Throws<InvalidOperationException>(() => original.ValidateLegacyMetadata(
                new BenchmarkRunMetadata { ModelId = "beam" }, true, false, false));
        }
    }
}
