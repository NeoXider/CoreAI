using System;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Benchmarking;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Unit tests for the portable benchmark scoring math (<see cref="GoalScore"/>): weight
    /// normalization, penalty floor, hard caps, the bonus eligibility gate, judge blending, and
    /// PASS/PARTIAL/FAIL classification including mandatory-checkpoint gates. Pure logic — no model,
    /// no Unity — so it runs under dotnet and guards against the "framework bug looks like a weak
    /// model" failure mode.
    /// </summary>
    public sealed class GoalScoreEditModeTests
    {
        private static BenchmarkCheckpoint Cp(string id, double weight, bool passed, bool mandatory = false)
        {
            return new BenchmarkCheckpoint(id, id, weight, passed, mandatory);
        }

        [Test]
        public void AllCheckpointsPassed_YieldsHundred_AndPass()
        {
            List<BenchmarkCheckpoint> cps = new()
            {
                Cp("a", 50, true),
                Cp("b", 50, true)
            };

            GoalScore score = GoalScore.Compute(cps);

            Assert.AreEqual(100, score.Base, 1e-6);
            Assert.AreEqual(BenchmarkClassification.Pass, score.Classification);
        }

        [Test]
        public void Weights_AreNormalizedToHundred_RegardlessOfScale()
        {
            // Arbitrary weight scale (sums to 8, not 100) must still yield a 0..100 score.
            List<BenchmarkCheckpoint> cps = new()
            {
                Cp("a", 6, true),
                Cp("b", 2, false)
            };

            GoalScore score = GoalScore.Compute(cps);

            Assert.AreEqual(75, score.Base, 1e-6);
            Assert.AreEqual(BenchmarkClassification.Partial, score.Classification);
        }

        [Test]
        public void Penalties_SubtractButNeverGoNegative()
        {
            List<BenchmarkCheckpoint> cps = new() { Cp("a", 100, true) };
            List<BenchmarkPenalty> penalties = new() { new BenchmarkPenalty("excess tools", 250) };

            GoalScore score = GoalScore.Compute(cps, penalties);

            Assert.AreEqual(0, score.Base, 1e-6);
            Assert.AreEqual(BenchmarkClassification.Fail, score.Classification);
        }

        [Test]
        public void HardCap_LimitsBase_WhenFinalAssertionFails()
        {
            List<BenchmarkCheckpoint> cps = new() { Cp("a", 100, true) };

            GoalScore score = GoalScore.Compute(cps, hardCap: 60);

            Assert.AreEqual(60, score.Base, 1e-6);
            Assert.AreEqual(BenchmarkClassification.Partial, score.Classification);
        }

        [Test]
        public void Bonus_IsGated_OnNearPerfectBase()
        {
            // Base 80 (< 90 eligibility) must not receive any bonus.
            List<BenchmarkCheckpoint> below = new() { Cp("a", 80, true), Cp("b", 20, false) };
            GoalScore noBonus = GoalScore.Compute(below, rawBonus: 10);
            Assert.AreEqual(80, noBonus.Base, 1e-6);
            Assert.AreEqual(0, noBonus.Bonus, 1e-6);
            Assert.AreEqual(80, noBonus.Total, 1e-6);

            // Base 100 is eligible; bonus is clamped to MaxBonus.
            List<BenchmarkCheckpoint> perfect = new() { Cp("a", 100, true) };
            GoalScore withBonus = GoalScore.Compute(perfect, rawBonus: 999);
            Assert.AreEqual(100, withBonus.Base, 1e-6);
            Assert.AreEqual(GoalScore.MaxBonus, withBonus.Bonus, 1e-6);
            Assert.AreEqual(100 + GoalScore.MaxBonus, withBonus.Total, 1e-6);
        }

        [Test]
        public void MandatoryCheckpointFailure_CapsVerdictAtPartial_EvenAtHighBase()
        {
            // 95/100 by weight, but the failed checkpoint is mandatory -> cannot be PASS.
            List<BenchmarkCheckpoint> cps = new()
            {
                Cp("main", 95, true),
                Cp("gate", 5, false, true)
            };

            GoalScore score = GoalScore.Compute(cps);

            Assert.AreEqual(95, score.Base, 1e-6);
            Assert.AreEqual(BenchmarkClassification.Partial, score.Classification);
        }

        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        public void BenchmarkMandatoryGates_SurviveMeanAggregationAndReportImport(bool extreme, int repetitions)
        {
            BenchmarkReport report = new();
            for (int repetition = 1; repetition <= repetitions; repetition++)
            {
                BenchmarkCheckpoint[] checkpoints = { Cp("main", 95, true), Cp("gate", 5, repetition > 1, true) };
                report.Add(new ScenarioResult
                {
                    ScenarioId = "gated", ScenarioName = "Gated scenario", Group = "G2", Extreme = extreme,
                    Repetition = repetition, Checkpoints = checkpoints, Score = GoalScore.Compute(checkpoints)
                });
            }

            string json = BenchmarkReportFormatter.ToJson(report);
            BenchmarkReport restored = BenchmarkRunStore.ImportReport(json).ToReport();
            foreach (BenchmarkReport candidate in new[] { report, restored })
            {
                Assert.AreEqual(repetitions == 1 ? 95 : 97.5, candidate.SuiteBaseScore, 1e-6);
                Assert.AreEqual(BenchmarkClassification.Partial, candidate.Scenarios()[0].Classification);
                Assert.AreEqual(0, candidate.PassCount);
                Assert.AreEqual(0, candidate.PassRate);
                Assert.AreEqual(1, candidate.PartialCount);
                Assert.AreEqual(0, candidate.GroupBreakdown()[0].PassCount);
                Assert.AreEqual(0, candidate.ExtremePassCount);
                Assert.AreEqual(0, candidate.GroupBreakdown()[0].ExtremePassCount);
                StringAssert.Contains("PASS 0 / PARTIAL 1 / FAIL 0", BenchmarkReportFormatter.ToMarkdown(candidate));
                if (extreme) StringAssert.Contains("(0/1 pass)", BenchmarkReportFormatter.ToMarkdown(candidate));
                JObject summary = JObject.Parse(BenchmarkReportFormatter.ToJson(candidate));
                Assert.AreEqual(0, (int)summary["summary"]["pass"]);
                Assert.AreEqual(0, (int)summary["summary"]["groups"]["G2"]["pass"]);
            }

            JObject fallback = JObject.Parse(json);
            ((JObject)fallback["summary"]).Remove("extremeScenarios");
            ((JObject)fallback["summary"]).Remove("groups");
            ModelSummary model = ModelComparison.ParseSummaryJson(fallback.ToString());
            Assert.AreEqual(0, model.ExtremePass, "Raw-row reconstruction must retain mandatory gates.");
            Assert.AreEqual(1, model.Scenarios);
        }

        [Test]
        public void BenchmarkRepetitionMean_DoesNotRequireEveryIndividualNumericScoreToPass()
        {
            BenchmarkReport report = new();
            foreach (int points in new[] { 80, 100 })
            {
                BenchmarkCheckpoint[] checkpoints = { Cp("earned", points, true), Cp("missed", 100 - points, false) };
                report.Add(new ScenarioResult
                {
                    ScenarioId = "mean", Score = GoalScore.Compute(checkpoints), Checkpoints = checkpoints
                });
            }
            Assert.AreEqual(90, report.SuiteBaseScore);
            Assert.AreEqual(1, report.PassCount, "Mandatory obligations survive; numeric classification still uses the mean.");
        }

        [TestCase(FailureAttribution.Model)]
        [TestCase(FailureAttribution.Environment)]
        [TestCase(FailureAttribution.Framework)]
        public void BenchmarkExcludedDeadline_PreservesAttributionWithoutAffectingScoresOrRoleSpeed(FailureAttribution attribution)
        {
            BenchmarkCheckpoint[] measured = Enum.GetValues(typeof(BenchmarkDimension)).Cast<BenchmarkDimension>()
                .Select(d => new BenchmarkCheckpoint(d.ToString(), d.ToString(), 10, true, dimension: d)).ToArray();
            BenchmarkReport report = new();
            report.Add(new ScenarioResult
            {
                ScenarioId = "measured", Group = "G2", Score = GoalScore.Compute(measured), Checkpoints = measured,
                CompletionTokens = 10, GenerationMs = 1000
            });
            double[] ratings = RoleFitness.Evaluate(report).Roles.Select(r => r.Rating).ToArray();
            report.Add(new ScenarioResult
            {
                ScenarioId = "custom", Group = "G6", Extreme = true, ExcludeFromScoring = true,
                Attribution = attribution, TimedOut = true, Failure = "scripted deadline", ToolCalls = 3,
                CompletionTokens = 1000000, GenerationMs = 1,
                Checkpoints = new[] { Cp("unrelated", 100, false, true) }
            });

            BenchmarkReport restored = BenchmarkRunStore.ImportReport(BenchmarkReportFormatter.ToJson(report)).ToReport();
            foreach (BenchmarkReport candidate in new[] { report, restored })
            {
                Assert.AreEqual(attribution, candidate.Results[1].Attribution);
                Assert.IsTrue(candidate.Results[1].ExcludeFromScoring);
                Assert.AreEqual(2, candidate.Results.Count);
                Assert.AreEqual(1, candidate.ScenarioCount);
                Assert.AreEqual(100, candidate.SuiteBaseScore);
                Assert.AreEqual(0, candidate.ExtremeScenarioCount);
                Assert.AreEqual(1, candidate.GroupBreakdown().Count);
                Assert.AreEqual(6, candidate.DimensionBreakdown().Count);
                Assert.IsTrue(candidate.DimensionBreakdown().All(d => d.Score == 100));
                CollectionAssert.AreEqual(ratings, RoleFitness.Evaluate(candidate).Roles.Select(r => r.Rating));
                Assert.AreEqual(3, candidate.TotalToolCalls, "Unscored evidence must stay visible.");
            }

            JObject fallback = JObject.Parse(BenchmarkReportFormatter.ToJson(report));
            ((JObject)fallback["summary"]).Remove("extremeScenarios");
            ((JObject)fallback["summary"]).Remove("groups");
            ModelSummary summary = ModelComparison.ParseSummaryJson(fallback.ToString());
            Assert.AreEqual(1, summary.Scenarios);
            Assert.AreEqual(0, summary.ExtremeScenarios);
            Assert.AreEqual(1, summary.Groups.Count);
        }

        [Test]
        public void BenchmarkInterruptedAttempt_IsEvidenceWithoutFinalizationAndIsReplacedOnce()
        {
            BenchmarkPlannedScenario[] plan = { new BenchmarkPlannedScenario { Id = "partial", Group = "G2", Repetition = 1 } };
            BenchmarkTargetState state = new();
            state.Merge(new ScenarioResult
            {
                ScenarioId = "partial", Group = "G2", Interrupted = true,
                SessionTranscript = "partial answer", ToolTraceJsonl = "partial tool evidence", ToolCalls = 1
            }, plan);
            Assert.IsFalse(state.IsFinalized("partial", 1));
            Assert.AreEqual(0, state.ToReport().ScenarioCount);
            BenchmarkTargetState restored = BenchmarkRunStore.ImportReport(BenchmarkReportFormatter.ToJson(state.ToReport()));
            Assert.IsFalse(restored.IsFinalized("partial", 1));
            Assert.AreEqual("partial answer", restored.Results[0].SessionTranscript);
            Assert.IsTrue(restored.Results[0].Interrupted);
            StringAssert.Contains("INTERRUPTED", BenchmarkReportFormatter.ToMarkdown(state.ToReport()));
            BenchmarkCheckpoint[] checkpoints = { Cp("done", 1, true) };
            restored.Merge(new ScenarioResult
            {
                ScenarioId = "partial", Group = "G2", Score = GoalScore.Compute(checkpoints), Checkpoints = checkpoints
            }, plan);
            Assert.AreEqual(1, restored.Results.Count);
            Assert.IsTrue(restored.IsFinalized("partial", 1));
            Assert.AreEqual(1, restored.ToReport().PassCount);
        }

        [TestCase(null, 120)]
        [TestCase("NaN", 120)]
        [TestCase("Infinity", 120)]
        [TestCase("-10", 0)]
        [TestCase("0", 0)]
        [TestCase("23.5", 23.5)]
        [TestCase("5000", 3600)]
        public void BenchmarkStallThreshold_UsesFiniteNormalizedResumeIdentity(string raw, double expected)
        {
            Assert.AreEqual(expected, BenchmarkProviderFailurePolicy.NormalizeStallSeconds(raw));
            const string key = "COREAI_BENCHMARK_PROVIDER_STALL_SECONDS";
            Assert.IsTrue(BenchmarkRunManifest.OptionMatches(key, raw, expected.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            double differentNormalizedThreshold = expected == 0 ? 1 : 0;
            Assert.IsFalse(BenchmarkRunManifest.OptionMatches(key, raw, differentNormalizedThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.IsTrue(BenchmarkRunManifest.OptionMatches("COREAI_BENCHMARK_RETRIES", "0", "3"));
        }

        [Test]
        public void JudgeBlend_KeepsDeterministicAtLeastHalf()
        {
            List<BenchmarkCheckpoint> cps = new() { Cp("a", 100, false) }; // deterministic = 0
            // Request 100% judge weight; it must be clamped to 0.5, so base = 0.5*0 + 0.5*80 = 40.
            GoalScore score = GoalScore.Compute(cps, judgeScore: 80, judgeWeight: 1.0);

            Assert.AreEqual(0.5, score.JudgeWeight, 1e-6);
            Assert.AreEqual(40, score.Base, 1e-6);
        }

        [Test]
        public void EmptyCheckpoints_YieldZero_NotCrash()
        {
            GoalScore score = GoalScore.Compute(new List<BenchmarkCheckpoint>());
            Assert.AreEqual(0, score.Base, 1e-6);
            Assert.AreEqual(BenchmarkClassification.Fail, score.Classification);
        }

        [Test]
        public void Speed_DoesNotAddToScore()
        {
            List<BenchmarkCheckpoint> perfect = new() { Cp("a", 100, true) };
            // A fast, cheap run (10% of both budgets) earns NO extra points for speed: efficiency is a
            // reported tok/s metric, not a score, so a merely-faster model cannot look "smarter".
            GoalScore score = GoalScore.Compute(perfect, rawBonus: 0,
                actualTokens: 100, tokenBudget: 1000, actualMs: 1000, timeBudgetMs: 10000);

            Assert.AreEqual(100, score.Base, 1e-6);
            Assert.AreEqual(0, score.EfficiencyBonus, 1e-6, "speed must not add to the score");
            Assert.AreEqual(0, score.Bonus, 1e-6, "with no correctness bonus, a fast run scores no bonus");
        }

        [Test]
        public void CorrectnessBonus_IsGatedOnBase_AndCappedAtMaxBonus()
        {
            // Below the bonus gate: a fast cheap run that did not solve earns nothing.
            List<BenchmarkCheckpoint> below = new() { Cp("a", 80, true), Cp("b", 20, false) };
            GoalScore gated = GoalScore.Compute(below, actualTokens: 1, tokenBudget: 1000,
                actualMs: 1, timeBudgetMs: 10000);
            Assert.AreEqual(0, gated.Bonus, 1e-6);

            // The correctness bonus never exceeds MaxBonus (and speed adds nothing to it).
            List<BenchmarkCheckpoint> perfect = new() { Cp("a", 100, true) };
            GoalScore capped = GoalScore.Compute(perfect, rawBonus: 20,
                actualTokens: 1, tokenBudget: 1000, actualMs: 1, timeBudgetMs: 10000);
            Assert.AreEqual(GoalScore.MaxBonus, capped.Bonus, 1e-6);
        }

        [Test]
        public void NonFiniteInputs_DoNotPoisonScore()
        {
            List<BenchmarkCheckpoint> perfect = new() { Cp("a", 100, true) };
            List<BenchmarkPenalty> nanPenalty = new() { new BenchmarkPenalty("x", double.NaN) };

            GoalScore score = GoalScore.Compute(perfect, nanPenalty, double.NaN,
                judgeScore: double.NaN, judgeWeight: 0.3,
                actualTokens: double.NaN, tokenBudget: 1000, actualMs: double.PositiveInfinity, timeBudgetMs: 10000);

            Assert.AreEqual(100, score.Base, 1e-6, "NaN judge/penalty must not move the base");
            Assert.IsFalse(double.IsNaN(score.Total));
        }
    }
}
