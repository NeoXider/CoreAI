using System;
using System.Collections.Generic;

namespace CoreAI.Benchmarking
{
    /// <summary>
    /// Per-scenario verdict bucket. <see cref="Pass"/> requires a near-perfect score and all
    /// mandatory gates; <see cref="Partial"/> means useful progress; <see cref="Fail"/> means the
    /// goal was not achieved (or the run never produced a valid final state).
    /// </summary>
    public enum BenchmarkClassification
    {
        Fail = 0,
        Partial = 1,
        Pass = 2
    }

    /// <summary>
    /// Separates "the model did poorly" from "our harness/framework broke". Without this a framework
    /// bug reads as a weak model. Populated by the runner, never by the grader.
    /// </summary>
    public enum FailureAttribution
    {
        /// <summary>Goal reached or only normal model shortfall — nothing to attribute.</summary>
        None = 0,

        /// <summary>Parser / tool execution / memory / sink / assertion infrastructure broke.</summary>
        Framework = 1,

        /// <summary>Framework worked; the model skipped a tool, used wrong args, or answered badly.</summary>
        Model = 2,

        /// <summary>Backend unavailable, model load canceled, timeout before first token, etc.</summary>
        Environment = 3,

        /// <summary>
        /// Ran fine, but deliberately excluded from the model's score — e.g. a scenario whose prompt was
        /// fully overridden by an operator env var, so the built-in checkpoints (tuned for the default
        /// prompt) no longer describe the task that was actually asked. Still shown in screenshots/tool
        /// stats/session transcript, just excluded from <see cref="BenchmarkReport"/>'s graded aggregates.
        /// </summary>
        NotGraded = 4
    }

    /// <summary>
    /// Scoring dimension a checkpoint contributes to. Lets the report break one suite score into a few
    /// comparable axes. Adding a new dimension (for future groups like G3) only needs a new enum value
    /// plus checkpoints tagged with it — the aggregation and report adapt automatically.
    /// </summary>
    public enum BenchmarkDimension
    {
        /// <summary>Correct tool selection, valid arguments, no failed/invalid calls.</summary>
        ToolCorrectness = 0,

        /// <summary>Right intent and ordering (discovery step before action, correct sequence).</summary>
        IntentSequence = 1,

        /// <summary>Whether the goal was actually achieved (final state / slot behavior).</summary>
        TaskCompletion = 2,

        /// <summary>Same inputs produce the same outputs (stable, repeatable logic).</summary>
        Determinism = 3,

        /// <summary>
        /// Genuine reasoning: correctness on derived / non-obvious inputs the model had to work out
        /// itself (no spoon-fed code) — piecewise logic, recursion, multi-condition math, constraint
        /// satisfaction. This is the axis that separates "follows instructions" from "is smart".
        /// </summary>
        Reasoning = 4,

        /// <summary>
        /// Strict instruction-following: obeying explicit constraints under a subtractive score (cadence
        /// rules, prohibitions, exact counts, ordering, forbidden tools). Each violation costs points.
        /// </summary>
        InstructionAdherence = 5
    }

    /// <summary>
    /// One weighted, deterministically (or judge-) checkable sub-goal of a scenario, tagged with the
    /// <see cref="BenchmarkDimension"/> it measures. Weights across a scenario's checkpoints are
    /// normalized to 100 by <see cref="GoalScore.Compute"/>, so authors can use any convenient scale.
    /// </summary>
    public sealed class BenchmarkCheckpoint
    {
        public BenchmarkCheckpoint(string id, string description, double weight, bool passed,
            bool mandatory = false, string detail = null,
            BenchmarkDimension dimension = BenchmarkDimension.TaskCompletion)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Description = description ?? string.Empty;
            Weight = weight < 0 ? 0 : weight;
            Passed = passed;
            Mandatory = mandatory;
            Detail = detail ?? string.Empty;
            Dimension = dimension;
        }

        public string Id { get; }
        public string Description { get; }
        public double Weight { get; }
        public bool Passed { get; }

        /// <summary>A failed mandatory checkpoint caps the verdict at <see cref="BenchmarkClassification.Partial"/>.</summary>
        public bool Mandatory { get; }

        public string Detail { get; }

        /// <summary>Which summary axis this checkpoint scores.</summary>
        public BenchmarkDimension Dimension { get; }
    }

    /// <summary>A points deduction earned at runtime (extra tool calls, hallucinated tools, wasted turns).</summary>
    public sealed class BenchmarkPenalty
    {
        public BenchmarkPenalty(string reason, double points)
        {
            Reason = reason ?? string.Empty;
            Points = points < 0 ? 0 : points;
        }

        public string Reason { get; }

        /// <summary>Positive magnitude of the deduction (subtracted from the earned score).</summary>
        public double Points { get; }
    }

    /// <summary>
    /// Immutable result of grading one scenario: a normalized 0..100 <see cref="Base"/> score plus a
    /// bounded <see cref="Bonus"/> (only granted when the base is already near-perfect, so a bonus can
    /// never rescue a failed task). <see cref="Total"/> is reported separately and never used to
    /// compare suites — comparisons use <see cref="Base"/>.
    /// </summary>
    public readonly struct GoalScore
    {
        private GoalScore(double checkpointScore, double penalties, int? hardCap, double judgeScore,
            double judgeWeight, double mergedBase, double correctnessBonus, double tokenBonus,
            double timeBonus, double efficiencyBonus, double bonus, double @base, double total,
            BenchmarkClassification classification)
        {
            CheckpointScore = checkpointScore;
            Penalties = penalties;
            HardCap = hardCap;
            JudgeScore = judgeScore;
            JudgeWeight = judgeWeight;
            MergedBase = mergedBase;
            CorrectnessBonus = correctnessBonus;
            TokenBonus = tokenBonus;
            TimeBonus = timeBonus;
            EfficiencyBonus = efficiencyBonus;
            Bonus = bonus;
            Base = @base;
            Total = total;
            Classification = classification;
        }

        /// <summary>Weighted, normalized 0..100 deterministic score before penalties/caps.</summary>
        public double CheckpointScore { get; }

        /// <summary>Total penalty points subtracted.</summary>
        public double Penalties { get; }

        /// <summary>Hard cap applied (e.g. 60 when the final assertion failed), if any.</summary>
        public int? HardCap { get; }

        /// <summary>Optional LLM-judge score 0..100 for fuzzy goals (NaN when no judge ran).</summary>
        public double JudgeScore { get; }

        /// <summary>Weight given to the judge when merging (0 for purely deterministic scenarios).</summary>
        public double JudgeWeight { get; }

        /// <summary>Deterministic + judge blended score before penalties/caps.</summary>
        public double MergedBase { get; }

        /// <summary>Correctness/robustness/stretch portion of the bonus (the scenario's requested bonus).</summary>
        public double CorrectnessBonus { get; }

        /// <summary>Bonus points earned for finishing under the token budget (0..<see cref="MaxTokenBonus"/>).</summary>
        public double TokenBonus { get; }

        /// <summary>Bonus points earned for finishing under the time budget (0..<see cref="MaxTimeBonus"/>).</summary>
        public double TimeBonus { get; }

        /// <summary>Efficiency portion of the bonus earned for using fewer tokens and less time than budget.</summary>
        public double EfficiencyBonus { get; }

        /// <summary>Bonus 0..<see cref="MaxBonus"/> (correctness + efficiency), granted only when <see cref="Base"/> &gt;= <see cref="BonusEligibilityBase"/>.</summary>
        public double Bonus { get; }

        /// <summary>Primary comparable score, 0..100.</summary>
        public double Base { get; }

        /// <summary>Base + Bonus, for display only (can exceed 100).</summary>
        public double Total { get; }

        public BenchmarkClassification Classification { get; }

        /// <summary>Maximum total bonus (correctness + efficiency) a scenario may award.</summary>
        public const double MaxBonus = 20;

        /// <summary>Maximum bonus for finishing under the token budget.</summary>
        public const double MaxTokenBonus = 6;

        /// <summary>Maximum bonus for finishing under the time budget.</summary>
        public const double MaxTimeBonus = 6;

        /// <summary>A scenario must reach this base score before any bonus is counted.</summary>
        public const double BonusEligibilityBase = 90;

        /// <summary>Minimum base for a <see cref="BenchmarkClassification.Pass"/>.</summary>
        public const double PassBase = 90;

        /// <summary>Minimum base for a <see cref="BenchmarkClassification.Partial"/>.</summary>
        public const double PartialBase = 50;

        /// <summary>
        /// Computes a <see cref="GoalScore"/> from graded checkpoints, runtime penalties, an optional
        /// hard cap, an optional LLM-judge score, and a raw bonus.
        /// </summary>
        /// <param name="checkpoints">Graded sub-goals; weights are normalized to 100.</param>
        /// <param name="penalties">Runtime deductions (extra tools, hallucinations, wasted turns).</param>
        /// <param name="rawBonus">Requested bonus; clamped to 0..<see cref="MaxBonus"/> and gated on base.</param>
        /// <param name="hardCap">Optional ceiling (e.g. 60 if final state failed, 40 if prose-only).</param>
        /// <param name="judgeScore">Optional judge score 0..100; pass null for deterministic-only.</param>
        /// <param name="judgeWeight">Judge blend weight 0..0.5 (deterministic stays &gt;= 50%).</param>
        /// <param name="actualTokens">Tokens the run actually used (for the efficiency bonus). 0 disables.</param>
        /// <param name="tokenBudget">Token budget; finishing under it earns up to <see cref="MaxTokenBonus"/>. 0 disables.</param>
        /// <param name="actualMs">Wall-clock milliseconds the run took (for the efficiency bonus). 0 disables.</param>
        /// <param name="timeBudgetMs">Time budget; finishing under it earns up to <see cref="MaxTimeBonus"/>. 0 disables.</param>
        public static GoalScore Compute(
            IReadOnlyList<BenchmarkCheckpoint> checkpoints,
            IReadOnlyList<BenchmarkPenalty> penalties = null,
            double rawBonus = 0,
            int? hardCap = null,
            double? judgeScore = null,
            double judgeWeight = 0,
            double actualTokens = 0,
            double tokenBudget = 0,
            double actualMs = 0,
            double timeBudgetMs = 0)
        {
            // Sanitize so a single non-finite input can never poison a score.
            rawBonus = Finite(rawBonus);
            judgeWeight = Finite(judgeWeight);
            if (hardCap.HasValue)
            {
                int hc = hardCap.Value;
                hardCap = hc < 0 ? 0 : hc > 100 ? 100 : hc;
            }

            double totalWeight = 0;
            double earnedWeight = 0;
            if (checkpoints != null)
            {
                foreach (BenchmarkCheckpoint cp in checkpoints)
                {
                    totalWeight += cp.Weight;
                    if (cp.Passed)
                    {
                        earnedWeight += cp.Weight;
                    }
                }
            }

            double checkpointScore = totalWeight > 0 ? earnedWeight / totalWeight * 100.0 : 0.0;

            // Blend judge in, keeping deterministic weight at >= 50%.
            double clampedJudgeWeight = Clamp(judgeWeight, 0, 0.5);
            double judge = judgeScore ?? double.NaN;
            double mergedBase = checkpointScore;
            // A NaN/Inf judge score means the judge did not run — ignore it rather than poisoning the base.
            bool judgeUsable = judgeScore.HasValue && !double.IsNaN(judgeScore.Value)
                                                   && !double.IsInfinity(judgeScore.Value);
            if (judgeUsable && clampedJudgeWeight > 0)
            {
                double j = Clamp(Finite(judgeScore.Value), 0, 100);
                mergedBase = (1.0 - clampedJudgeWeight) * checkpointScore + clampedJudgeWeight * j;
            }

            double penaltyPoints = 0;
            if (penalties != null)
            {
                foreach (BenchmarkPenalty p in penalties)
                {
                    penaltyPoints += Finite(p.Points);
                }
            }

            double baseScore = Clamp(mergedBase - penaltyPoints, 0, 100);
            if (hardCap.HasValue && baseScore > hardCap.Value)
            {
                baseScore = hardCap.Value;
            }

            // Bonus is gated: a task must already be near-perfect to earn it. Among solvers, reward the
            // efficient ones — fewer tokens and less time than budget add on top of the correctness bonus,
            // with the whole bonus capped at MaxBonus.
            double correctnessBonus = 0;
            double tokenBonus = 0;
            double timeBonus = 0;
            double efficiencyBonus = 0;
            double bonus = 0;
            if (baseScore >= BonusEligibilityBase)
            {
                correctnessBonus = Clamp(rawBonus, 0, MaxBonus);
                tokenBonus = tokenBudget > 0
                    ? MaxTokenBonus * Clamp((tokenBudget - Finite(actualTokens)) / tokenBudget, 0, 1)
                    : 0;
                timeBonus = timeBudgetMs > 0
                    ? MaxTimeBonus * Clamp((timeBudgetMs - Finite(actualMs)) / timeBudgetMs, 0, 1)
                    : 0;

                bonus = Clamp(correctnessBonus + tokenBonus + timeBonus, 0, MaxBonus);
                efficiencyBonus = bonus - Math.Min(correctnessBonus, bonus);
            }

            bool allMandatoryPassed = true;
            if (checkpoints != null)
            {
                foreach (BenchmarkCheckpoint cp in checkpoints)
                {
                    if (cp.Mandatory && !cp.Passed)
                    {
                        allMandatoryPassed = false;
                        break;
                    }
                }
            }

            BenchmarkClassification classification;
            if (baseScore >= PassBase && allMandatoryPassed)
            {
                classification = BenchmarkClassification.Pass;
            }
            else if (baseScore >= PartialBase)
            {
                classification = BenchmarkClassification.Partial;
            }
            else
            {
                classification = BenchmarkClassification.Fail;
            }

            return new GoalScore(checkpointScore, penaltyPoints, hardCap, judge, clampedJudgeWeight,
                mergedBase, correctnessBonus, tokenBonus, timeBonus, efficiencyBonus, bonus, baseScore,
                baseScore + bonus, classification);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value))
            {
                return min;
            }

            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        private static double Finite(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
        }
    }
}