using System.Collections.Generic;
using CoreAI.Benchmarking;
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
        public void EfficiencyBonus_RewardsFewerTokensAndLessTime()
        {
            List<BenchmarkCheckpoint> perfect = new() { Cp("a", 100, true) };
            // Used 10% of both budgets -> ~0.9 of each efficiency band (6 each) on top of correctness.
            GoalScore score = GoalScore.Compute(perfect, rawBonus: 0,
                actualTokens: 100, tokenBudget: 1000, actualMs: 1000, timeBudgetMs: 10000);

            Assert.AreEqual(100, score.Base, 1e-6);
            Assert.AreEqual(10.8, score.EfficiencyBonus, 1e-6); // 5.4 token + 5.4 time
            Assert.AreEqual(10.8, score.Bonus, 1e-6);
        }

        [Test]
        public void EfficiencyBonus_IsGatedOnBase_AndCappedWithCorrectness()
        {
            // Below the bonus gate: a fast cheap run that did not solve earns nothing.
            List<BenchmarkCheckpoint> below = new() { Cp("a", 80, true), Cp("b", 20, false) };
            GoalScore gated = GoalScore.Compute(below, actualTokens: 1, tokenBudget: 1000,
                actualMs: 1, timeBudgetMs: 10000);
            Assert.AreEqual(0, gated.Bonus, 1e-6);

            // Correctness + efficiency together never exceed MaxBonus.
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