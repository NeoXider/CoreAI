using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreAI.Benchmarking
{
    /// <summary>
    /// Reproducibility metadata captured once per run: which model/config produced these numbers.
    /// </summary>
    public sealed class BenchmarkRunMetadata
    {
        public string RunId { get; set; } = string.Empty;
        public string TimestampUtc { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string Backend { get; set; } = string.Empty;
        public bool NativeToolCalling { get; set; }
        public bool Streaming { get; set; }
        public int MaxParallelToolCalls { get; set; }
        public float Temperature { get; set; }
        public int Repetitions { get; set; } = 1;
        public string UnityVersion { get; set; } = string.Empty;
        public string SuiteVersion { get; set; } = string.Empty;
    }

    /// <summary>
    /// Aggregated benchmark run: the metadata plus every <see cref="ScenarioResult"/>. Aggregates are
    /// computed over <see cref="Base"/> scores (the comparable number); bonus is reported separately.
    /// When a scenario is run multiple times, callers should add each repetition and read the
    /// per-scenario mean (average) via <see cref="MeanBaseByScenario"/>.
    /// </summary>
    public sealed class BenchmarkReport
    {
        public BenchmarkRunMetadata Metadata { get; set; } = new();
        public List<ScenarioResult> Results { get; } = new();

        public void Add(ScenarioResult result)
        {
            if (result != null)
            {
                Results.Add(result);
            }
        }

        /// <summary>
        /// Results that actually measure the model — excludes <see cref="FailureAttribution.Environment"/>
        /// (provider/transport crashes) and <see cref="FailureAttribution.Framework"/> (harness bugs), so
        /// infrastructure flakiness never corrupts the model's score. Those runs are still reported in the
        /// tool stats, sessions, and the failure banner.
        /// </summary>
        private IEnumerable<ScenarioResult> GradedResults =>
            Results.Where(r => r.Attribution != FailureAttribution.Environment
                               && r.Attribution != FailureAttribution.Framework);

        /// <summary>
        /// One row per distinct scenario that produced a real model measurement, aggregated across
        /// repetitions via the MEAN (average) base score over its repetitions. Suite-level numbers are
        /// computed from these.
        /// </summary>
        public IReadOnlyList<ScenarioSummary> Scenarios()
        {
            List<ScenarioSummary> summaries = new();
            foreach (IGrouping<string, ScenarioResult> group in GradedResults.GroupBy(r => r.ScenarioId))
            {
                double meanBase = group.Average(r => r.Score.Base);
                double meanBonus = group.Average(r => r.Score.Bonus);
                ScenarioResult any = group.First();
                summaries.Add(new ScenarioSummary
                {
                    ScenarioId = group.Key,
                    Name = any.ScenarioName,
                    Group = any.Group,
                    Repetitions = group.Count(),
                    MeanBase = meanBase,
                    MeanBonus = meanBonus,
                    Spread = group.Max(r => r.Score.Base) - group.Min(r => r.Score.Base),
                    Classification = Classify(meanBase)
                });
            }

            return summaries;
        }

        private static BenchmarkClassification Classify(double meanBase)
        {
            if (meanBase >= GoalScore.PassBase)
            {
                return BenchmarkClassification.Pass;
            }

            return meanBase >= GoalScore.PartialBase
                ? BenchmarkClassification.Partial
                : BenchmarkClassification.Fail;
        }

        /// <summary>Suite score: mean of the per-scenario mean (average) base scores.</summary>
        public double SuiteBaseScore
        {
            get
            {
                IReadOnlyList<ScenarioSummary> s = Scenarios();
                return s.Count == 0 ? 0 : s.Average(x => x.MeanBase);
            }
        }

        /// <summary>Mean of per-scenario mean bonus, reported separately from the suite base score.</summary>
        public double MeanBonus
        {
            get
            {
                IReadOnlyList<ScenarioSummary> s = Scenarios();
                return s.Count == 0 ? 0 : s.Average(x => x.MeanBonus);
            }
        }

        public double PassRate
        {
            get
            {
                IReadOnlyList<ScenarioSummary> s = Scenarios();
                return s.Count == 0
                    ? 0
                    : (double)s.Count(x => x.Classification == BenchmarkClassification.Pass) / s.Count;
            }
        }

        public int PassCount => Scenarios().Count(s => s.Classification == BenchmarkClassification.Pass);
        public int PartialCount => Scenarios().Count(s => s.Classification == BenchmarkClassification.Partial);
        public int FailCount => Scenarios().Count(s => s.Classification == BenchmarkClassification.Fail);

        public int FrameworkFailures => Results.Count(r => r.Attribution == FailureAttribution.Framework);
        public int EnvironmentFailures => Results.Count(r => r.Attribution == FailureAttribution.Environment);

        public long TotalTokens => Results.Sum(r => (long)r.TotalTokens);
        public long TotalPromptTokens => Results.Sum(r => (long)r.PromptTokens);
        public long TotalCompletionTokens => Results.Sum(r => (long)r.CompletionTokens);
        public double TotalCostUsd => Results.Where(r => r.CostKnown).Sum(r => r.CostUsd);
        public double TotalLatencyMs => Results.Sum(r => r.LatencyMs);

        /// <summary>Wall-clock spent inside LLM calls only (generation), summed across the suite.</summary>
        public double TotalGenerationMs => Results.Sum(r => r.GenerationMs);

        /// <summary>
        /// Provider-call throughput: COMPLETION tokens ÷ time spent INSIDE the LLM calls, which is
        /// <b>prefill + decode</b> (NOT decode-only), excluding tool execution, grading and orchestration.
        /// This is LOWER than the decode-only tok/s a runtime like LM Studio reports, because LM Studio
        /// excludes prompt prefill — and CoreAI's agentic prompts are large (often ~14x the output), so
        /// prefill dominates. True decode-only timing needs TTFT, which is only measurable on the streaming
        /// path (see Docs/TOKENS_PER_SEC_FIX_PLAN.md). Falls back to the session-wide rate if per-call timing
        /// is absent.
        /// </summary>
        public double GenerationTokensPerSecond =>
            TotalGenerationMs > 0 ? TotalCompletionTokens / (TotalGenerationMs / 1000.0)
            : TotalLatencyMs <= 0 ? 0 : TotalCompletionTokens / (TotalLatencyMs / 1000.0);

        /// <summary>
        /// End-to-end throughput across the whole agentic session (includes prompt prefill, tool execution
        /// and orchestration gaps) — always lower than <see cref="GenerationTokensPerSecond"/>.
        /// </summary>
        public double EffectiveTokensPerSecond =>
            TotalLatencyMs <= 0 ? 0 : TotalCompletionTokens / (TotalLatencyMs / 1000.0);

        // Tool-call statistics (shown before the session transcript).
        public int TotalToolCalls => Results.Sum(r => r.ToolCalls);
        public int TotalFailedToolCalls => Results.Sum(r => r.FailedToolCalls);
        public int TotalInvalidCommands => Results.Sum(r => r.InvalidCommands);

        public double ToolErrorRate
        {
            get
            {
                int calls = TotalToolCalls;
                return calls == 0 ? 0 : (double)TotalFailedToolCalls / calls;
            }
        }

        /// <summary>Mean efficiency bonus earned (token + time), over graded results only.</summary>
        public double MeanEfficiencyBonus => MeanGraded(r => r.Score.EfficiencyBonus);

        /// <summary>Mean bonus earned for fewer tokens than budget (graded results only).</summary>
        public double MeanTokenBonus => MeanGraded(r => r.Score.TokenBonus);

        /// <summary>Mean bonus earned for less time than budget (graded results only).</summary>
        public double MeanTimeBonus => MeanGraded(r => r.Score.TimeBonus);

        private double MeanGraded(Func<ScenarioResult, double> selector)
        {
            List<ScenarioResult> graded = GradedResults.ToList();
            return graded.Count == 0 ? 0 : graded.Average(selector);
        }

        /// <summary>
        /// Suite score split by <see cref="BenchmarkDimension"/>: the weighted pass-rate of every
        /// checkpoint tagged with that dimension, across all results. Dimensions with no checkpoints are
        /// omitted, so the report adapts as scenarios (and future groups) add new dimensions.
        /// </summary>
        public IReadOnlyList<DimensionScore> DimensionBreakdown()
        {
            // Scenario-normalized, consistent with the per-scenario-median suite score: compute each
            // scenario's per-dimension weighted pass-rate (averaged over its repetitions), then average
            // across scenarios. This stops a checkpoint-heavy scenario (or reps > 1) from dominating.
            Dictionary<BenchmarkDimension, List<double>> perScenario = new();

            foreach (IGrouping<string, ScenarioResult> scenario in GradedResults.GroupBy(r => r.ScenarioId))
            {
                Dictionary<BenchmarkDimension, List<double>> repScores = new();
                foreach (ScenarioResult r in scenario)
                {
                    Dictionary<BenchmarkDimension, double[]> acc = new(); // [0]=earned, [1]=total
                    foreach (BenchmarkCheckpoint cp in r.Checkpoints)
                    {
                        if (!acc.TryGetValue(cp.Dimension, out double[] cell))
                        {
                            cell = new double[2];
                            acc[cp.Dimension] = cell;
                        }

                        cell[1] += cp.Weight;
                        if (cp.Passed)
                        {
                            cell[0] += cp.Weight;
                        }
                    }

                    foreach (KeyValuePair<BenchmarkDimension, double[]> kv in acc)
                    {
                        double s = kv.Value[1] > 0 ? kv.Value[0] / kv.Value[1] * 100.0 : 0;
                        if (!repScores.TryGetValue(kv.Key, out List<double> reps))
                        {
                            reps = new List<double>();
                            repScores[kv.Key] = reps;
                        }

                        reps.Add(s);
                    }
                }

                foreach (KeyValuePair<BenchmarkDimension, List<double>> kv in repScores)
                {
                    if (!perScenario.TryGetValue(kv.Key, out List<double> list))
                    {
                        list = new List<double>();
                        perScenario[kv.Key] = list;
                    }

                    list.Add(kv.Value.Average()); // average across this scenario's repetitions
                }
            }

            List<DimensionScore> result = new();
            foreach (KeyValuePair<BenchmarkDimension, List<double>> kv in perScenario)
            {
                result.Add(new DimensionScore
                {
                    Dimension = kv.Key,
                    Score = kv.Value.Average(), // average across scenarios — each scenario counts once
                    Checkpoints = kv.Value.Count // scenarios contributing to this dimension
                });
            }

            return result.OrderBy(d => (int)d.Dimension).ToList();
        }

        /// <summary>Highest-scoring graded result (for the report scorecard), or null when none.</summary>
        public ScenarioResult Best =>
            GradedResults.OrderByDescending(r => r.Score.Base).FirstOrDefault();

        /// <summary>Lowest-scoring graded result (for the report scorecard), or null when none.</summary>
        public ScenarioResult Worst =>
            GradedResults.OrderBy(r => r.Score.Base).FirstOrDefault();

        /// <summary>Mean of per-scenario mean (average) base score per benchmark group (e.g. G1, G2).</summary>
        public IReadOnlyList<GroupScore> GroupBreakdown()
        {
            return Scenarios()
                .GroupBy(s => s.Group)
                .Select(g => new GroupScore
                {
                    Group = g.Key,
                    MeanBase = g.Average(s => s.MeanBase),
                    PassCount = g.Count(s => s.Classification == BenchmarkClassification.Pass),
                    Count = g.Count()
                })
                .OrderBy(g => g.Group, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Mean (average) base score per scenario id, averaged over its repetitions.</summary>
        public IReadOnlyDictionary<string, double> MeanBaseByScenario()
        {
            Dictionary<string, double> means = new();
            foreach (IGrouping<string, ScenarioResult> group in Results.GroupBy(r => r.ScenarioId))
            {
                means[group.Key] = group.Average(r => r.Score.Base);
            }

            return means;
        }
    }

    /// <summary>Mean base score and pass count for one benchmark group, used in the report scorecard.</summary>
    public sealed class GroupScore
    {
        public string Group { get; set; } = string.Empty;
        public double MeanBase { get; set; }
        public int PassCount { get; set; }
        public int Count { get; set; }
    }

    /// <summary>Suite score for one <see cref="BenchmarkDimension"/> (a summary axis).</summary>
    public sealed class DimensionScore
    {
        public BenchmarkDimension Dimension { get; set; }
        public double Score { get; set; }
        public int Checkpoints { get; set; }
    }

    /// <summary>A distinct scenario aggregated across its repetitions (mean base + spread).</summary>
    public sealed class ScenarioSummary
    {
        public string ScenarioId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public int Repetitions { get; set; }
        public double MeanBase { get; set; }
        public double MeanBonus { get; set; }

        /// <summary>max(base) − min(base) across repetitions — a stability indicator for noisy models.</summary>
        public double Spread { get; set; }

        public BenchmarkClassification Classification { get; set; }
    }
}
