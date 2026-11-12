using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using CoreAI.Mods.Performance;

namespace CoreAI.PerfBench
{
    /// <summary>
    /// Entry point. Options: <c>--json &lt;path&gt;</c> (or env <c>COREAI_PERF_JSON</c>), <c>--label &lt;text&gt;</c>,
    /// <c>--only &lt;substring&gt;</c> (micro-benchmark filter), <c>--scenarios &lt;list|all|none&gt;</c>,
    /// <c>--no-micro</c>, <c>--trials &lt;n&gt;</c>, <c>--trial-seconds &lt;s&gt;</c>, <c>--warmup &lt;frames&gt;</c>,
    /// <c>--frames &lt;frames&gt;</c>, <c>--quick</c> (short trials; never evidence),
    /// <c>--alloc-trace &lt;substring&gt;</c> with <c>--alloc-ops &lt;n&gt;</c> (sampled allocation types of the matching cases, CoreCLR only).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            SynchronizationContext.SetSynchronizationContext(null);

            string jsonPath = Environment.GetEnvironmentVariable("COREAI_PERF_JSON");
            string label = "";
            string only = null;
            string scenarioList = "all";
            bool micro = true;
            int trials = 7;
            double trialSeconds = 0.25d;
            int warmupFrames = 120;
            int measuredFrames = 600;
            bool quick = false;
            string allocTrace = null;
            int allocOps = 20000;
            for (int index = 0; index < args.Length; index++)
            {
                string arg = args[index];
                string next = index + 1 < args.Length ? args[index + 1] : null;
                switch (arg)
                {
                    case "--json":
                        jsonPath = next;
                        index++;
                        break;
                    case "--label":
                        label = next ?? "";
                        index++;
                        break;
                    case "--only":
                        only = next;
                        index++;
                        break;
                    case "--scenarios":
                        scenarioList = next ?? "all";
                        index++;
                        break;
                    case "--no-micro":
                        micro = false;
                        break;
                    case "--trials":
                        trials = int.Parse(next, CultureInfo.InvariantCulture);
                        index++;
                        break;
                    case "--trial-seconds":
                        trialSeconds = double.Parse(next, CultureInfo.InvariantCulture);
                        index++;
                        break;
                    case "--warmup":
                        warmupFrames = int.Parse(next, CultureInfo.InvariantCulture);
                        index++;
                        break;
                    case "--frames":
                        measuredFrames = int.Parse(next, CultureInfo.InvariantCulture);
                        index++;
                        break;
                    case "--quick":
                        quick = true;
                        break;
                    case "--alloc-trace":
                        allocTrace = next;
                        index++;
                        break;
                    case "--alloc-ops":
                        allocOps = int.Parse(next, CultureInfo.InvariantCulture);
                        index++;
                        break;
                    default:
                        Console.Error.WriteLine("unknown option " + arg);
                        return 2;
                }
            }

            if (quick)
            {
                trials = 3;
                trialSeconds = 0.05d;
                warmupFrames = 10;
                measuredFrames = 60;
            }

            if (allocTrace != null)
            {
                foreach (MicroCase microCase in MicroCases.Build())
                {
                    if (microCase.Name.IndexOf(allocTrace, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        AllocationTrace.Report(microCase, allocOps);
                    }
                }

                return 0;
            }

            string repoRoot = BenchEnvironment.FindRepoRoot();
            bool countAllocations = BenchEnvironment.AllocationCounterWorks();
            PerfRunReport report = new();
            BenchEnvironment.Describe(report, repoRoot, label);
            report.SetEnvironment("quick", quick ? "true (not evidence)" : "false");
            foreach (KeyValuePair<string, string> fact in report.Environment)
            {
                Console.WriteLine(fact.Key + "=" + fact.Value);
            }

            List<MicroResult> microResults = new();
            if (micro)
            {
                Console.WriteLine();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-12} {1,-72} {2,14} {3,12} {4,10}",
                    "group", "case", "ns/op (med)", "bytes/op", "ops/trial"));
                foreach (MicroCase microCase in MicroCases.Build())
                {
                    if (only != null && microCase.Name.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0
                                     && microCase.Group.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    MicroResult result = MicroBench.Run(microCase, trialSeconds, trials, countAllocations);
                    microResults.Add(result);
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0,-12} {1,-72} {2,14:F1} {3,12} {4,10}{5}",
                        result.Group, result.Name, result.MedianNanosecondsPerOp,
                        result.BytesPerOp < 0 ? "n/a" : result.BytesPerOp.ToString("F1", CultureInfo.InvariantCulture),
                        result.OpsPerTrial, result.Error == null ? "" : "  ERROR " + result.Error));
                    if (result.Details.Length > 0)
                    {
                        Console.WriteLine("             " + result.Details);
                    }
                }
            }

            if (!string.Equals(scenarioList, "none", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-24} {1,9} {2,9} {3,9} {4,9} {5,12} {6,12} {7,10} {8,8}",
                    "scenario", "p50 ms", "p95 ms", "p99 ms", "max ms", "B/frame p50", "B/frame max", "steps/frm", "res/frm"));
                string harness = BenchEnvironment.IsMono ? "portable-mono" : "portable-coreclr";
                foreach (PerfScenario scenario in PerfScenarioCatalog.ParseList(scenarioList))
                {
                    PerfScenarioResult result = ScenarioFrames.Run(scenario, repoRoot, warmupFrames, measuredFrames,
                        countAllocations, harness);
                    report.Scenarios.Add(result);
                    double frames = Math.Max(1, result.MeasuredFrames);
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0,-24} {1,9:F3} {2,9:F3} {3,9:F3} {4,9:F3} {5,12} {6,12} {7,10:F0} {8,8:F1}",
                        scenario.Id, result.FrameMilliseconds.P50, result.FrameMilliseconds.P95,
                        result.FrameMilliseconds.P99, result.FrameMilliseconds.Max,
                        countAllocations ? result.GcAllocatedBytesPerFrame.P50.ToString("F0", CultureInfo.InvariantCulture) : "n/a",
                        countAllocations ? result.GcAllocatedBytesPerFrame.Max.ToString("F0", CultureInfo.InvariantCulture) : "n/a",
                        result.Lua.GuardedSteps / frames, result.Lua.ThreadResumes / frames));
                    for (int index = 0; index < result.FailedMods.Count; index++)
                    {
                        Console.WriteLine("    failed mod: " + result.FailedMods[index]);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(jsonPath))
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(jsonPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(jsonPath, WriteJson(report, microResults), new UTF8Encoding(false));
                Console.WriteLine();
                Console.WriteLine("json=" + Path.GetFullPath(jsonPath));
            }

            return 0;
        }

        private static string WriteJson(PerfRunReport report, List<MicroResult> microResults)
        {
            StringBuilder json = new();
            json.Append("{\n  \"schema\": \"coreai-perfbench/1\",\n  \"micro\": [");
            for (int index = 0; index < microResults.Count; index++)
            {
                MicroResult result = microResults[index];
                json.Append(index == 0 ? "\n    {" : ",\n    {");
                json.Append("\"group\": ");
                PerfReportJson.AppendString(json, result.Group);
                json.Append(", \"name\": ");
                PerfReportJson.AppendString(json, result.Name);
                json.Append(", \"medianNsPerOp\": ").Append(PerfReportJson.FormatDouble(result.MedianNanosecondsPerOp));
                json.Append(", \"minNsPerOp\": ").Append(PerfReportJson.FormatDouble(result.MinNanosecondsPerOp));
                json.Append(", \"maxNsPerOp\": ").Append(PerfReportJson.FormatDouble(result.MaxNanosecondsPerOp));
                json.Append(", \"bytesPerOp\": ").Append(result.BytesPerOp < 0 ? "null" : PerfReportJson.FormatDouble(result.BytesPerOp));
                json.Append(", \"opsPerTrial\": ").Append(result.OpsPerTrial.ToString(CultureInfo.InvariantCulture));
                json.Append(", \"trials\": ").Append(result.Trials.ToString(CultureInfo.InvariantCulture));
                json.Append(", \"gen0Collections\": ").Append(result.Gen0Collections.ToString(CultureInfo.InvariantCulture));
                json.Append(", \"notes\": ");
                PerfReportJson.AppendString(json, result.Notes);
                json.Append(", \"details\": ");
                PerfReportJson.AppendString(json, result.Details);
                json.Append(", \"error\": ");
                if (result.Error == null)
                {
                    json.Append("null");
                }
                else
                {
                    PerfReportJson.AppendString(json, result.Error);
                }

                json.Append('}');
            }

            json.Append(microResults.Count == 0 ? "],\n" : "\n  ],\n");
            json.Append("  \"scenarioReport\": ");
            json.Append(PerfReportJson.Write(report).TrimEnd());
            json.Append("\n}\n");
            return json.ToString();
        }
    }
}
