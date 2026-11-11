using System;
using System.Collections;
using System.Collections.Generic;
using CoreAI.Mods.Performance;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.Performance
{
    /// <summary>
    /// P19 measurement fixtures: each test builds one reference scene in code, runs it and records frame-time
    /// percentiles, garbage per frame, batches, draw calls, memory and Lua throughput. Explicit, so the
    /// ordinary gates never run them; run them by name (dev-docs/PERF_BASELINE_2026-11.md, "Commands").
    /// </summary>
    /// <remarks>
    /// The report is printed to the test output and, when <c>COREAI_PERF_JSON</c> names a file, rewritten
    /// there after every test with every scene measured so far in this domain. <c>COREAI_PERF_FRAMES</c>,
    /// <c>COREAI_PERF_WARMUP</c>, <c>COREAI_PERF_LABEL</c> and <c>COREAI_PERF_COMMIT</c> set the same
    /// options the player runner reads. A test fails only when its scene could not be built or a measured
    /// frame threw; a slow frame is a number for the table, not a failure (the budgets come with the fixes).
    /// </remarks>
    [TestFixture]
    [Explicit("P19 measurement: run by name, never in the ordinary gates")]
    [Category("Performance")]
    public sealed class ModsPerformancePlayModeTests
    {
        private static readonly PerfRunReport Report = new();
        private static bool _environmentDescribed;

        [UnityTest]
        public IEnumerator IdleWorld()
        {
            return Measure(PerfScenarioCatalog.IdleWorld);
        }

        [UnityTest]
        public IEnumerator SampleMods()
        {
            return Measure(PerfScenarioCatalog.SampleMods);
        }

        [UnityTest]
        public IEnumerator Parts2048Static()
        {
            return Measure(PerfScenarioCatalog.Parts2048Static);
        }

        [UnityTest]
        public IEnumerator Parts2048Heartbeat()
        {
            return Measure(PerfScenarioCatalog.Parts2048Heartbeat);
        }

        [UnityTest]
        public IEnumerator Parts16384Static()
        {
            return Measure(PerfScenarioCatalog.Parts16384Static);
        }

        [UnityTest]
        public IEnumerator Parts16384Heartbeat()
        {
            return Measure(PerfScenarioCatalog.Parts16384Heartbeat);
        }

        private static IEnumerator Measure(PerfScenario scenario)
        {
            // WHY inside the test body: the Unity Test Framework re-arms its log check per test, and a scene's
            // own error logs (a missing optional shader, a sample mod's warning) are data for the report here,
            // counted in errorLogs, not a reason to throw the measurement away.
            LogAssert.ignoreFailingMessages = true;
            PerfRunnerOptions options = PerfRunnerOptions.Read(null, Environment.GetEnvironmentVariable, null);
            if (!_environmentDescribed)
            {
                PerfUnityEnvironment.Describe(Report, options);
                Report.SetEnvironment("harness", "Editor PlayMode, [Explicit] ModsPerformancePlayModeTests");
                _environmentDescribed = true;
            }

            PerfScenarioResult result = null;
            yield return PerfScenarioRunner.RunScenario(scenario, options, "unity-playmode", measured => result = measured);

            Assert.IsNotNull(result, "the runner must report the scene");
            Report.Scenarios.RemoveAll(existing => existing.Scenario.Id == scenario.Id);
            Report.Scenarios.Add(result);
            string written = PerfScenarioRunner.Publish(Report, options.OutputPath);
            TestContext.WriteLine(PerfReportJson.Write(SingleSceneReport(result)));
            if (written.Length > 0)
            {
                TestContext.WriteLine("report: " + written);
            }

            Assert.IsEmpty(result.Failure, result.Failure);
            Assert.IsFalse(result.Notes.Exists(note => note.StartsWith("frames whose Advance threw", StringComparison.Ordinal)),
                "a measured frame threw: " + string.Join(" | ", result.Notes));
            if (result.FailedMods.Count > 0)
            {
                TestContext.WriteLine("mods that failed to load: " + string.Join(" | ", result.FailedMods));
            }

            Debug.Log("[CoreAI.Perf] " + scenario.Id + ": frame p50 " + result.FrameMilliseconds.P50.ToString("F3")
                      + " ms, p99 " + result.FrameMilliseconds.P99.ToString("F3") + " ms, GC/frame p50 "
                      + result.GcAllocatedBytesPerFrame.P50.ToString("F0") + " B (" + result.GcAllocatedSource + ")");
        }

        private static PerfRunReport SingleSceneReport(PerfScenarioResult result)
        {
            PerfRunReport single = new();
            foreach (KeyValuePair<string, string> fact in Report.Environment)
            {
                single.SetEnvironment(fact.Key, fact.Value);
            }

            single.Scenarios.Add(result);
            return single;
        }
    }
}
