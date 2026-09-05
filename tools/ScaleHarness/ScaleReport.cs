using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CoreAI.Tools.Scale
{
    /// <summary>One measured frame of the main loop, in milliseconds and bytes.</summary>
    public sealed class ScaleFrameSample
    {
        public double TotalMs;
        public double OrchestratorMs;
        public double SchedulerMs;
        public double SignalsMs;
        public double NetworkMs;
        public double ModRuntimeMs;
        public long AllocBytes;
        public long AllocSignalsBytes;
        public long GuardedSteps;
        public long ThreadResumes;
        public long PacketsSent;
        public long PayloadBytes;
        public double WallPeriodMs;
    }

    /// <summary>One chat request observed end to end through the production orchestrator.</summary>
    public sealed class ScaleChatRecord
    {
        public string ActorId = "";
        public string TraceId = "";
        public string Pattern = "";
        public int OfferedFrame;
        public long OfferedTimestamp;
        public long ProviderStartedTimestamp;
        public long CompletedTimestamp;
        public string Outcome = "pending";
        public string Error = "";

        public double QueueWaitMs => ProviderStartedTimestamp > OfferedTimestamp
            ? ScaleMath.TicksToMs(ProviderStartedTimestamp - OfferedTimestamp)
            : 0d;

        public double EndToEndMs => CompletedTimestamp > OfferedTimestamp
            ? ScaleMath.TicksToMs(CompletedTimestamp - OfferedTimestamp)
            : 0d;
    }

    /// <summary>Per-actor Lua-side counters read from the mod store at a snapshot boundary.</summary>
    public sealed class ScaleActorCounters
    {
        public string ActorId { get; set; } = "";

        public long Heartbeats { get; set; }

        public long Sent { get; set; }

        public long Acks { get; set; }

        public long Spawned { get; set; }

        public long Loops { get; set; }
    }

    /// <summary>Distribution summary of a metric.</summary>
    public sealed class ScaleDistribution
    {
        public int Count { get; set; }

        public double Min { get; set; }

        public double Median { get; set; }

        public double Mean { get; set; }

        public double P95 { get; set; }

        public double P99 { get; set; }

        public double Max { get; set; }

        public static ScaleDistribution Of(IEnumerable<double> values)
        {
            List<double> sorted = values.ToList();
            sorted.Sort();
            if (sorted.Count == 0)
            {
                return new ScaleDistribution();
            }

            return new ScaleDistribution
            {
                Count = sorted.Count,
                Min = sorted[0],
                Median = ScaleMath.Percentile(sorted, 0.5d),
                Mean = sorted.Average(),
                P95 = ScaleMath.Percentile(sorted, 0.95d),
                P99 = ScaleMath.Percentile(sorted, 0.99d),
                Max = sorted[sorted.Count - 1]
            };
        }
    }

    /// <summary>Chat outcome counters and wait distributions for one arrival pattern.</summary>
    public sealed class ScaleChatPatternSummary
    {
        public string Pattern { get; set; } = "";

        public int Offered { get; set; }

        public int Served { get; set; }

        public int AdmissionRefusals { get; set; }

        public int Cancelled { get; set; }

        public int Failed { get; set; }

        public int Pending { get; set; }

        public ScaleDistribution QueueWaitMs { get; set; } = new ScaleDistribution();

        public ScaleDistribution EndToEndMs { get; set; } = new ScaleDistribution();
    }

    /// <summary>Everything measured in one (actorCount, repeat) run.</summary>
    public sealed class ScaleRepeatResult
    {
        public int ActorCount { get; set; }

        public int Repeat { get; set; }

        public int MeasuredFrames { get; set; }

        public double MeasurementWallSeconds { get; set; }

        public ScaleDistribution TotalMs { get; set; } = new ScaleDistribution();

        public ScaleDistribution OrchestratorMs { get; set; } = new ScaleDistribution();

        public ScaleDistribution SchedulerMs { get; set; } = new ScaleDistribution();

        public ScaleDistribution SignalsMs { get; set; } = new ScaleDistribution();

        public ScaleDistribution NetworkMs { get; set; } = new ScaleDistribution();

        public ScaleDistribution ModRuntimeMs { get; set; } = new ScaleDistribution();

        public ScaleDistribution AllocBytesPerFrame { get; set; } = new ScaleDistribution();

        public ScaleDistribution AllocSignalsBytesPerFrame { get; set; } = new ScaleDistribution();

        public ScaleDistribution GuardedStepsPerFrame { get; set; } = new ScaleDistribution();

        public ScaleDistribution ThreadResumesPerFrame { get; set; } = new ScaleDistribution();

        public ScaleDistribution PacketsPerFrame { get; set; } = new ScaleDistribution();

        public ScaleDistribution WallPeriodMs { get; set; } = new ScaleDistribution();

        public long GuardedStepsTotal { get; set; }

        public long ThreadResumesTotal { get; set; }

        public long SnapshotEventsDelivered { get; set; }

        public long CompletedOperationsTotal { get; set; }

        public long PacketsSentTotal { get; set; }

        public long PacketsDeliveredTotal { get; set; }

        public long PayloadBytesTotal { get; set; }

        public long RateRefusals { get; set; }

        public long BridgeOtherRefusals { get; set; }

        public long HeartbeatsTotal { get; set; }

        public long RemoteSentTotal { get; set; }

        public long AcksTotal { get; set; }

        public long ServerReceivedTotal { get; set; }

        public long PartsSpawnedTotal { get; set; }

        public long WaitLoopResumesTotal { get; set; }

        public long HandlerErrors { get; set; }

        public List<string> HandlerErrorSamples { get; set; } = new List<string>();

        public int LiveInstancesAtEnd { get; set; }

        public double HeapStartBytes { get; set; }

        public double HeapEndBytes { get; set; }

        /// <summary>
        /// Reported, never gated: its noise on identical repeats reaches 47 MB/min against a budget
        /// of 1, so it cannot decide pass or fail. See <see cref="RetainedHeapDeltaMegabytes"/>.
        /// </summary>
        public double HeapSlopeMegabytesPerMinute { get; set; }

        /// <summary>Live retained bytes at the start of the window, after a stabilising collection.</summary>
        public double RetainedHeapStartBytes { get; set; }

        /// <summary>Live retained bytes at the end of the window, after a stabilising collection.</summary>
        public double RetainedHeapEndBytes { get; set; }

        /// <summary>
        /// Growth in LIVE retained memory across the measured window. This is the honest leak signal:
        /// both endpoints are taken after a full collect + finalizer drain, so collector timing cannot
        /// move it the way it moves the slope.
        /// </summary>
        public double RetainedHeapDeltaMegabytes =>
            (RetainedHeapEndBytes - RetainedHeapStartBytes) / (1024d * 1024d);

        public double WorkingSetStartBytes { get; set; }

        public double WorkingSetEndBytes { get; set; }

        public double WorkingSetSlopeMegabytesPerMinute { get; set; }

        public int Gen0Collections { get; set; }

        public int Gen1Collections { get; set; }

        public int Gen2Collections { get; set; }

        public double FairnessHeartbeatMaxMinRatio { get; set; }

        public double FairnessAckMaxMinRatio { get; set; }

        public double FairnessChatEndToEndMaxMinRatio { get; set; }

        public List<ScaleChatPatternSummary> Chat { get; set; } = new List<ScaleChatPatternSummary>();

        public int ChatDeadlineCancellations { get; set; }

        public int ChatSameActorReplacements { get; set; }

        public double DrainWallSeconds { get; set; }

        public long SyncContextContinuations { get; set; }

        public Dictionary<string, long> Counters { get; set; } = new Dictionary<string, long>();

        public List<string> ZeroCounters { get; set; } = new List<string>();

        public ScaleFrameSeries Frames { get; set; } = new ScaleFrameSeries();
    }

    /// <summary>Raw per-frame series for the measured window (kept for re-analysis).</summary>
    public sealed class ScaleFrameSeries
    {
        public List<double> TotalMs { get; set; } = new List<double>();

        public List<double> OrchestratorMs { get; set; } = new List<double>();

        public List<double> SchedulerMs { get; set; } = new List<double>();

        public List<double> SignalsMs { get; set; } = new List<double>();

        public List<double> NetworkMs { get; set; } = new List<double>();

        public List<double> ModRuntimeMs { get; set; } = new List<double>();

        public List<long> AllocBytes { get; set; } = new List<long>();

        public List<long> GuardedSteps { get; set; } = new List<long>();

        public List<long> PacketsSent { get; set; } = new List<long>();
    }

    /// <summary>Median-of-repeats and worst-of-repeats for one staircase step.</summary>
    public sealed class ScaleStepSummary
    {
        public int ActorCount { get; set; }

        public int Repeats { get; set; }

        public double MedianTotalMs { get; set; }

        public double WorstRepeatMedianTotalMs { get; set; }

        public double MedianP99TotalMs { get; set; }

        public double WorstTotalMs { get; set; }

        public double MedianOrchestratorMs { get; set; }

        public double MedianSchedulerMs { get; set; }

        public double MedianSignalsMs { get; set; }

        public double MedianNetworkMs { get; set; }

        public double MedianModRuntimeMs { get; set; }

        public double WorstOrchestratorMs { get; set; }

        public double WorstSchedulerMs { get; set; }

        public double WorstSignalsMs { get; set; }

        public double WorstNetworkMs { get; set; }

        public double WorstModRuntimeMs { get; set; }

        public double MedianGuardedStepsPerFrame { get; set; }

        public double MedianThreadResumesPerFrame { get; set; }

        public double MedianAllocBytesPerFrame { get; set; }

        public double WorstAllocBytesPerFrame { get; set; }

        public double MedianPacketsPerFrame { get; set; }

        public long PacketsSentTotal { get; set; }

        public long PayloadBytesTotal { get; set; }

        public double WorstHeapSlopeMegabytesPerMinute { get; set; }

        public double WorstWorkingSetSlopeMegabytesPerMinute { get; set; }

        public long RateRefusals { get; set; }

        public long HandlerErrors { get; set; }

        public int ChatAdmissionRefusals { get; set; }

        public int ChatOffered { get; set; }

        public int ChatServed { get; set; }

        public double WorstChatBurstP95EndToEndMs { get; set; }

        public double WorstChatStaggeredP95EndToEndMs { get; set; }

        public double WorstChatBurstP95QueueWaitMs { get; set; }

        public double WorstChatStaggeredP95QueueWaitMs { get; set; }

        public double WorstFairnessHeartbeatMaxMinRatio { get; set; }

        public double WorstFairnessAckMaxMinRatio { get; set; }

        public double WorstFairnessChatMaxMinRatio { get; set; }

        public List<string> ZeroCounters { get; set; } = new List<string>();

        public Dictionary<string, bool> FrameBudgetPass { get; set; } = new Dictionary<string, bool>();

        public bool ChatGatePass { get; set; }

        public bool HeapGatePass { get; set; }

        /// <summary>Worst growth in live retained megabytes across repeats — one half of the memory gate.</summary>
        public double WorstRetainedHeapDeltaMegabytes { get; set; }

        /// <summary>Worst bytes allocated per actor per frame across repeats — the other half.</summary>
        public double WorstAllocBytesPerActorPerFrame { get; set; }

        public bool WorkCountersPass { get; set; }

        public Dictionary<string, bool> OverallPass { get; set; } = new Dictionary<string, bool>();
    }

    /// <summary>Linear fit of median frame time against actor count.</summary>
    public sealed class ScaleFrameCostFit
    {
        public double InterceptMs { get; set; }

        public double PerActorMs { get; set; }

        public double RSquared { get; set; }

        public Dictionary<string, double> ProjectedActorsWithinBudget { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>Machine-readable staircase output.</summary>
    public sealed class ScaleStaircaseReport
    {
        public string SchemaVersion { get; set; } = "scale-staircase-v1";

        public string Label { get; set; } = "";

        public DateTime StartedUtc { get; set; }

        public DateTime CompletedUtc { get; set; }

        public bool FrozenWorkloadHonoured { get; set; }

        public string WorkloadPath { get; set; } = "";

        public string WorkloadSha256 { get; set; } = "";

        public string ActorLuaSha256 { get; set; } = "";

        public string ServerLuaSha256 { get; set; } = "";

        public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();

        public ScaleWorkload Workload { get; set; }

        public List<ScaleRepeatResult> Repeats { get; set; } = new List<ScaleRepeatResult>();

        public List<ScaleStepSummary> Steps { get; set; } = new List<ScaleStepSummary>();

        public ScaleFrameCostFit Fit { get; set; } = new ScaleFrameCostFit();

        public Dictionary<string, int> LargestPassingActorCount { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>Percentile, regression, and unit helpers shared by the runner and the report.</summary>
    public static class ScaleMath
    {
        public static double TicksToMs(long ticks)
        {
            return ticks * 1000d / System.Diagnostics.Stopwatch.Frequency;
        }

        public static double Percentile(List<double> sorted, double fraction)
        {
            if (sorted.Count == 0)
            {
                return 0d;
            }

            double rank = fraction * (sorted.Count - 1);
            int low = (int)Math.Floor(rank);
            int high = (int)Math.Ceiling(rank);
            if (low == high)
            {
                return sorted[low];
            }

            double weight = rank - low;
            return sorted[low] * (1d - weight) + sorted[high] * weight;
        }

        public static double Median(IEnumerable<double> values)
        {
            List<double> sorted = values.ToList();
            sorted.Sort();
            return Percentile(sorted, 0.5d);
        }

        /// <summary>Least-squares slope of y against x; returns 0 for fewer than two points.</summary>
        public static double Slope(IReadOnlyList<double> xs, IReadOnlyList<double> ys, out double intercept,
            out double rSquared)
        {
            intercept = 0d;
            rSquared = 0d;
            if (xs.Count < 2 || xs.Count != ys.Count)
            {
                return 0d;
            }

            double meanX = xs.Average();
            double meanY = ys.Average();
            double sxx = 0d;
            double sxy = 0d;
            double syy = 0d;
            for (int index = 0; index < xs.Count; index++)
            {
                double dx = xs[index] - meanX;
                double dy = ys[index] - meanY;
                sxx += dx * dx;
                sxy += dx * dy;
                syy += dy * dy;
            }

            if (sxx <= 0d)
            {
                return 0d;
            }

            double slope = sxy / sxx;
            intercept = meanY - slope * meanX;
            rSquared = syy <= 0d ? 1d : (sxy * sxy) / (sxx * syy);
            return slope;
        }

        public static double MaxMinRatio(IEnumerable<double> values)
        {
            List<double> list = values.ToList();
            if (list.Count == 0)
            {
                return 0d;
            }

            double min = list.Min();
            double max = list.Max();
            if (min <= 0d)
            {
                return max <= 0d ? 1d : double.PositiveInfinity;
            }

            return max / min;
        }
    }

    /// <summary>Builds step summaries, the linear fit, and the Markdown table from repeat results.</summary>
    public static class ScaleReportBuilder
    {
        public static void Summarize(ScaleStaircaseReport report)
        {
            ScaleWorkload workload = report.Workload;
            report.Steps.Clear();
            foreach (IGrouping<int, ScaleRepeatResult> group in report.Repeats
                         .GroupBy(repeat => repeat.ActorCount)
                         .OrderBy(group => group.Key))
            {
                List<ScaleRepeatResult> repeats = group.ToList();
                ScaleStepSummary step = new ScaleStepSummary
                {
                    ActorCount = group.Key,
                    Repeats = repeats.Count,
                    MedianTotalMs = ScaleMath.Median(repeats.Select(r => r.TotalMs.Median)),
                    WorstRepeatMedianTotalMs = repeats.Max(r => r.TotalMs.Median),
                    MedianP99TotalMs = ScaleMath.Median(repeats.Select(r => r.TotalMs.P99)),
                    WorstTotalMs = repeats.Max(r => r.TotalMs.Max),
                    MedianOrchestratorMs = ScaleMath.Median(repeats.Select(r => r.OrchestratorMs.Median)),
                    MedianSchedulerMs = ScaleMath.Median(repeats.Select(r => r.SchedulerMs.Median)),
                    MedianSignalsMs = ScaleMath.Median(repeats.Select(r => r.SignalsMs.Median)),
                    MedianNetworkMs = ScaleMath.Median(repeats.Select(r => r.NetworkMs.Median)),
                    MedianModRuntimeMs = ScaleMath.Median(repeats.Select(r => r.ModRuntimeMs.Median)),
                    WorstOrchestratorMs = repeats.Max(r => r.OrchestratorMs.Max),
                    WorstSchedulerMs = repeats.Max(r => r.SchedulerMs.Max),
                    WorstSignalsMs = repeats.Max(r => r.SignalsMs.Max),
                    WorstNetworkMs = repeats.Max(r => r.NetworkMs.Max),
                    WorstModRuntimeMs = repeats.Max(r => r.ModRuntimeMs.Max),
                    MedianGuardedStepsPerFrame = ScaleMath.Median(repeats.Select(r => r.GuardedStepsPerFrame.Median)),
                    MedianThreadResumesPerFrame = ScaleMath.Median(repeats.Select(r => r.ThreadResumesPerFrame.Median)),
                    MedianAllocBytesPerFrame = ScaleMath.Median(repeats.Select(r => r.AllocBytesPerFrame.Median)),
                    WorstAllocBytesPerFrame = repeats.Max(r => r.AllocBytesPerFrame.Max),
                    MedianPacketsPerFrame = ScaleMath.Median(repeats.Select(r => r.PacketsPerFrame.Median)),
                    PacketsSentTotal = repeats.Sum(r => r.PacketsSentTotal),
                    PayloadBytesTotal = repeats.Sum(r => r.PayloadBytesTotal),
                    WorstHeapSlopeMegabytesPerMinute = repeats.Max(r => r.HeapSlopeMegabytesPerMinute),
                    WorstRetainedHeapDeltaMegabytes = repeats.Max(r => r.RetainedHeapDeltaMegabytes),
                    WorstAllocBytesPerActorPerFrame = repeats.Max(
                        r => r.AllocBytesPerFrame.Median / Math.Max(1, group.Key)),
                    WorstWorkingSetSlopeMegabytesPerMinute = repeats.Max(r => r.WorkingSetSlopeMegabytesPerMinute),
                    RateRefusals = repeats.Sum(r => r.RateRefusals),
                    HandlerErrors = repeats.Sum(r => r.HandlerErrors),
                    ChatAdmissionRefusals = repeats.Sum(r => r.Chat.Sum(c => c.AdmissionRefusals)),
                    ChatOffered = repeats.Sum(r => r.Chat.Sum(c => c.Offered)),
                    ChatServed = repeats.Sum(r => r.Chat.Sum(c => c.Served)),
                    WorstChatBurstP95EndToEndMs = WorstChat(repeats, "burst", c => c.EndToEndMs.P95),
                    WorstChatStaggeredP95EndToEndMs = WorstChat(repeats, "staggered", c => c.EndToEndMs.P95),
                    WorstChatBurstP95QueueWaitMs = WorstChat(repeats, "burst", c => c.QueueWaitMs.P95),
                    WorstChatStaggeredP95QueueWaitMs = WorstChat(repeats, "staggered", c => c.QueueWaitMs.P95),
                    WorstFairnessHeartbeatMaxMinRatio = repeats.Max(r => r.FairnessHeartbeatMaxMinRatio),
                    WorstFairnessAckMaxMinRatio = repeats.Max(r => r.FairnessAckMaxMinRatio),
                    WorstFairnessChatMaxMinRatio = repeats.Max(r => r.FairnessChatEndToEndMaxMinRatio),
                    ZeroCounters = repeats.SelectMany(r => r.ZeroCounters).Distinct().OrderBy(x => x).ToList()
                };

                step.WorkCountersPass = step.ZeroCounters.Count == 0;
                step.ChatGatePass = step.ChatAdmissionRefusals == 0
                                    && step.ChatServed == step.ChatOffered
                                    && step.ChatOffered > 0
                                    && step.WorstChatBurstP95EndToEndMs <= workload.Chat.P95EndToEndBudgetMilliseconds
                                    && step.WorstChatStaggeredP95EndToEndMs <= workload.Chat.P95EndToEndBudgetMilliseconds;
                // WHY the slope stopped deciding this: three identical repeats at N=100 produced
                // -45.6, -70.4 and -93.3 MB/min against a 1 MB/min budget. It measures when the
                // collector ran. Retained delta (stabilised endpoints) and allocation rate per actor
                // decide it now; both are reproducible, the second to the byte.
                step.HeapGatePass =
                    step.WorstRetainedHeapDeltaMegabytes <= workload.Budgets.RetainedHeapDeltaMegabytesMax
                    && step.WorstAllocBytesPerActorPerFrame <= workload.Budgets.AllocBytesPerActorPerFrameMax;
                foreach (double budget in workload.Budgets.FrameMilliseconds)
                {
                    string key = FormatBudget(budget);
                    bool framePass = step.WorstRepeatMedianTotalMs <= budget;
                    step.FrameBudgetPass[key] = framePass;
                    step.OverallPass[key] = framePass && step.WorkCountersPass && step.ChatGatePass && step.HeapGatePass;
                }

                report.Steps.Add(step);
            }

            List<double> xs = report.Steps.Select(step => (double)step.ActorCount).ToList();
            List<double> ys = report.Steps.Select(step => step.MedianTotalMs).ToList();
            double slope = ScaleMath.Slope(xs, ys, out double intercept, out double rSquared);
            report.Fit = new ScaleFrameCostFit
            {
                InterceptMs = intercept,
                PerActorMs = slope,
                RSquared = rSquared
            };
            report.LargestPassingActorCount.Clear();
            foreach (double budget in workload.Budgets.FrameMilliseconds)
            {
                string key = FormatBudget(budget);
                report.Fit.ProjectedActorsWithinBudget[key] = slope > 0d
                    ? Math.Floor((budget - intercept) / slope)
                    : double.PositiveInfinity;
                int largest = 0;
                foreach (ScaleStepSummary step in report.Steps)
                {
                    if (step.OverallPass.TryGetValue(key, out bool pass) && pass)
                    {
                        largest = Math.Max(largest, step.ActorCount);
                    }
                }

                report.LargestPassingActorCount[key] = largest;
            }
        }

        public static string ToMarkdown(ScaleStaircaseReport report)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("| N | median frame ms | worst-repeat median ms | median p99 ms | worst frame ms | orch / sched / signals / net / modrt (median ms) | worst per phase ms | steps/frame | resumes/frame | alloc/frame (median / worst) | packets/frame | heap MB/min | chat offered/served/refused | burst p95 e2e ms | staggered p95 e2e ms | fairness hb / ack / chat | zero counters | PASS 4 ms | PASS 16 ms |");
            sb.AppendLine("|---:|---:|---:|---:|---:|---|---|---:|---:|---|---:|---:|---|---:|---:|---|---|---|---|");
            foreach (ScaleStepSummary step in report.Steps)
            {
                sb.Append("| ").Append(step.ActorCount)
                    .Append(" | ").Append(step.MedianTotalMs.ToString("0.000", ci))
                    .Append(" | ").Append(step.WorstRepeatMedianTotalMs.ToString("0.000", ci))
                    .Append(" | ").Append(step.MedianP99TotalMs.ToString("0.000", ci))
                    .Append(" | ").Append(step.WorstTotalMs.ToString("0.000", ci))
                    .Append(" | ").Append(step.MedianOrchestratorMs.ToString("0.000", ci))
                    .Append(" / ").Append(step.MedianSchedulerMs.ToString("0.000", ci))
                    .Append(" / ").Append(step.MedianSignalsMs.ToString("0.000", ci))
                    .Append(" / ").Append(step.MedianNetworkMs.ToString("0.000", ci))
                    .Append(" / ").Append(step.MedianModRuntimeMs.ToString("0.000", ci))
                    .Append(" | ").Append(step.WorstOrchestratorMs.ToString("0.00", ci))
                    .Append(" / ").Append(step.WorstSchedulerMs.ToString("0.00", ci))
                    .Append(" / ").Append(step.WorstSignalsMs.ToString("0.00", ci))
                    .Append(" / ").Append(step.WorstNetworkMs.ToString("0.00", ci))
                    .Append(" / ").Append(step.WorstModRuntimeMs.ToString("0.00", ci))
                    .Append(" | ").Append(step.MedianGuardedStepsPerFrame.ToString("0", ci))
                    .Append(" | ").Append(step.MedianThreadResumesPerFrame.ToString("0", ci))
                    .Append(" | ").Append(FormatBytes(step.MedianAllocBytesPerFrame))
                    .Append(" / ").Append(FormatBytes(step.WorstAllocBytesPerFrame))
                    .Append(" | ").Append(step.MedianPacketsPerFrame.ToString("0.0", ci))
                    .Append(" | ").Append(step.WorstHeapSlopeMegabytesPerMinute.ToString("0.00", ci))
                    .Append(" | ").Append(step.ChatOffered).Append('/').Append(step.ChatServed).Append('/').Append(step.ChatAdmissionRefusals)
                    .Append(" | ").Append(step.WorstChatBurstP95EndToEndMs.ToString("0", ci))
                    .Append(" | ").Append(step.WorstChatStaggeredP95EndToEndMs.ToString("0", ci))
                    .Append(" | ").Append(FormatRatio(step.WorstFairnessHeartbeatMaxMinRatio))
                    .Append(" / ").Append(FormatRatio(step.WorstFairnessAckMaxMinRatio))
                    .Append(" / ").Append(FormatRatio(step.WorstFairnessChatMaxMinRatio))
                    .Append(" | ").Append(step.ZeroCounters.Count == 0 ? "none" : string.Join(",", step.ZeroCounters))
                    .Append(" | ").Append(PassText(step, "4ms"))
                    .Append(" | ").Append(PassText(step, "16ms"))
                    .AppendLine(" |");
            }

            sb.AppendLine();
            sb.Append("Linear fit of median frame ms against N: ")
                .Append(report.Fit.InterceptMs.ToString("0.000", ci)).Append(" ms + ")
                .Append(report.Fit.PerActorMs.ToString("0.0000", ci)).Append(" ms/actor (R^2 ")
                .Append(report.Fit.RSquared.ToString("0.000", ci)).AppendLine(").");
            foreach (KeyValuePair<string, double> pair in report.Fit.ProjectedActorsWithinBudget)
            {
                sb.Append("Projected actors inside ").Append(pair.Key).Append(": ")
                    .Append(double.IsInfinity(pair.Value) ? "unbounded" : pair.Value.ToString("0", ci))
                    .Append("; largest measured passing N: ")
                    .Append(report.LargestPassingActorCount.TryGetValue(pair.Key, out int largest) ? largest.ToString(ci) : "0")
                    .AppendLine(".");
            }

            return sb.ToString();
        }

        private static string PassText(ScaleStepSummary step, string key)
        {
            if (!step.OverallPass.TryGetValue(key, out bool pass))
            {
                return "n/a";
            }

            if (pass)
            {
                return "PASS";
            }

            List<string> reasons = new List<string>();
            if (step.FrameBudgetPass.TryGetValue(key, out bool frame) && !frame)
            {
                reasons.Add("frame");
            }

            if (!step.WorkCountersPass)
            {
                reasons.Add("zero-work");
            }

            if (!step.ChatGatePass)
            {
                reasons.Add("chat");
            }

            if (!step.HeapGatePass)
            {
                reasons.Add("heap");
            }

            return "FAIL(" + string.Join("+", reasons) + ")";
        }

        private static double WorstChat(IReadOnlyList<ScaleRepeatResult> repeats, string pattern,
            Func<ScaleChatPatternSummary, double> selector)
        {
            double worst = 0d;
            foreach (ScaleRepeatResult repeat in repeats)
            {
                foreach (ScaleChatPatternSummary summary in repeat.Chat)
                {
                    if (string.Equals(summary.Pattern, pattern, StringComparison.Ordinal))
                    {
                        worst = Math.Max(worst, selector(summary));
                    }
                }
            }

            return worst;
        }

        public static string FormatBudget(double budget)
        {
            return budget.ToString("0.##", CultureInfo.InvariantCulture) + "ms";
        }

        private static string FormatBytes(double bytes)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            if (bytes >= 1024d * 1024d)
            {
                return (bytes / (1024d * 1024d)).ToString("0.00", ci) + " MB";
            }

            if (bytes >= 1024d)
            {
                return (bytes / 1024d).ToString("0.0", ci) + " KB";
            }

            return bytes.ToString("0", ci) + " B";
        }

        private static string FormatRatio(double ratio)
        {
            return double.IsInfinity(ratio) ? "inf" : ratio.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
