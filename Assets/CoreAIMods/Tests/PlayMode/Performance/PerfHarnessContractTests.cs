using System;
using System.Collections.Generic;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Performance;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.PlayMode.Performance
{
    /// <summary>
    /// The P19 harness's own contract, engine-free so it runs in every ordinary gate (Unity PlayMode and the
    /// portable leg, tools/perfbench/Tests): percentile maths, the report format, the scenario catalog, and a
    /// headless smoke run of a reference scene through the production mod stack. The measuring fixtures
    /// themselves are [Explicit] (see <see cref="ModsPerformancePlayModeTests"/>).
    /// </summary>
    [TestFixture]
    public sealed class PerfHarnessContractTests
    {
        private System.Threading.SynchronizationContext _savedContext;

        /// <summary>
        /// WHY detached, as the sibling Lua fixtures do: the smoke test drives the VM synchronously from the
        /// test thread, and a continuation posted to Unity's context would wait for the very frame this
        /// [Test] is holding.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public void NearestRank_OneToHundred_ReportsObservedValues()
        {
            double[] samples = new double[100];
            for (int index = 0; index < samples.Length; index++)
            {
                samples[index] = index + 1;
            }

            Assert.AreEqual(50d, PerfDistribution.NearestRank(samples, 50d));
            Assert.AreEqual(95d, PerfDistribution.NearestRank(samples, 95d));
            Assert.AreEqual(99d, PerfDistribution.NearestRank(samples, 99d));
            Assert.AreEqual(1d, PerfDistribution.NearestRank(samples, 0d));
            Assert.AreEqual(100d, PerfDistribution.NearestRank(samples, 100d));
        }

        [Test]
        public void NearestRank_SmallSeries_NeverInterpolates()
        {
            Assert.AreEqual(7d, PerfDistribution.NearestRank(new[] { 7d }, 99d));
            Assert.AreEqual(1d, PerfDistribution.NearestRank(new[] { 1d, 9d }, 50d),
                "the median of two samples is the lower observed one, not their mean");
            Assert.AreEqual(9d, PerfDistribution.NearestRank(new[] { 1d, 9d }, 95d));
        }

        [Test]
        public void Distribution_FromUnsortedSeries_SummarisesWithoutChangingTheInput()
        {
            List<double> samples = new() { 4d, 1d, 3d, 2d };
            PerfDistribution distribution = PerfDistribution.From(samples);
            Assert.AreEqual(4, distribution.Count);
            Assert.AreEqual(2.5d, distribution.Mean, 1e-12);
            Assert.AreEqual(1d, distribution.Min);
            Assert.AreEqual(4d, distribution.Max);
            Assert.AreEqual(2d, distribution.P50);
            Assert.AreEqual(4d, distribution.P99);
            CollectionAssert.AreEqual(new[] { 4d, 1d, 3d, 2d }, samples, "the caller's series must stay as recorded");
        }

        [Test]
        public void Distribution_EmptyOrNaN_IsUnmeasuredOrRefused()
        {
            Assert.AreEqual(0, PerfDistribution.From(new List<double>()).Count);
            Assert.AreEqual(0, PerfDistribution.From(null).Count);
            Assert.Throws<ArgumentException>(() => PerfDistribution.From(new List<double> { 1d, double.NaN }));
        }

        [Test]
        public void ReportJson_UnmeasuredSeries_AreNullNeverZero()
        {
            PerfRunReport report = new();
            report.SetEnvironment("commit", "abc");
            report.SetEnvironment("commit", "def");
            PerfScenarioResult result = new(PerfScenarioCatalog.IdleWorld, "unit-test")
            {
                MeasuredFrames = 2,
                FrameMilliseconds = PerfDistribution.From(new List<double> { 1.5d, 2.5d })
            };
            result.Unavailable.Add("Batches Count");
            result.Notes.Add("quote \" and backslash \\ and newline \n survive");
            report.Scenarios.Add(result);

            string json = PerfReportJson.Write(report);

            StringAssert.Contains("\"schema\": \"coreai-perf/1\"", json);
            StringAssert.Contains("\"commit\": \"def\"", json, "a repeated environment key replaces the old value");
            StringAssert.DoesNotContain("\"abc\"", json);
            StringAssert.Contains("\"gcAllocatedBytesPerFrame\": null", json);
            StringAssert.Contains("\"batches\": null", json);
            StringAssert.Contains("\"p50\": 1.5", json);
            StringAssert.Contains("quote \\\" and backslash \\\\ and newline \\n survive", json);
            Assert.AreEqual(Count(json, '{'), Count(json, '}'), "braces must balance");
            Assert.AreEqual(Count(json, '['), Count(json, ']'), "brackets must balance");
#if !UNITY_5_3_OR_NEWER
            using (System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json))
            {
                Assert.AreEqual(1, document.RootElement.GetProperty("scenarios").GetArrayLength());
            }
#endif
        }

        [Test]
        public void ReportJson_ForLog_IsFramedByTheMarkers()
        {
            string text = PerfReportJson.WriteForLog(new PerfRunReport());
            StringAssert.StartsWith(PerfReportJson.LogBeginMarker + "\n", text);
            StringAssert.EndsWith(PerfReportJson.LogEndMarker, text);
            Assert.AreEqual("null", PerfReportJson.FormatDouble(double.NaN));
            Assert.AreEqual("null", PerfReportJson.FormatDouble(double.PositiveInfinity));
        }

        [Test]
        public void Catalog_IdsAreUnique_AndListsParseStrictly()
        {
            HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
            foreach (PerfScenario scenario in PerfScenarioCatalog.All)
            {
                Assert.IsTrue(ids.Add(scenario.Id), "duplicate scenario id " + scenario.Id);
            }

            Assert.AreEqual(PerfScenarioCatalog.All.Count, PerfScenarioCatalog.ParseList("all").Count);
            Assert.AreEqual(PerfScenarioCatalog.All.Count, PerfScenarioCatalog.ParseList("").Count);
            IReadOnlyList<PerfScenario> two = PerfScenarioCatalog.ParseList("idle-world, PARTS-2048-STATIC,idle-world");
            Assert.AreEqual(2, two.Count);
            Assert.Throws<ArgumentException>(() => PerfScenarioCatalog.ParseList("idle-world,typo"));
        }

        [Test]
        public void Catalog_SixteenThousandScenes_FitExactlyUnderTheDesktopInstanceCeiling()
        {
            Assert.AreEqual(2048, PerfScenarioCatalog.Parts2048Static.TotalPartCount);
            Assert.AreEqual(2048, PerfScenarioCatalog.Parts2048Heartbeat.TotalPartCount);
            Assert.AreEqual(LuaCsModRuntime.EmergencyMaxRegisteredInstances, PerfScenarioCatalog.Parts16384Static.TotalPartCount);
            Assert.AreEqual(LuaCsModRuntime.EmergencyMaxRegisteredInstances,
                PerfScenarioCatalog.Parts16384Heartbeat.TotalPartCount,
                "the 16,384 scenes use the whole desktop ceiling; a folder or a helper instance would not fit");
        }

        [Test]
        public void MoverMod_SplitsAcrossHandlers_AndMovesEveryPartEachFrame()
        {
            PerfRuntimeCounters counters = new();
            InstanceRegistry registry = new(binder: new InMemoryInstanceBackingBinder());
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            InMemoryPartPropertySink sink = new(registry);
            PerfScenarioResult result = new(PerfScenarioCatalog.Parts2048Heartbeat, "unit-test");
            using (PerfModWorld world = new(registry, game, sink, counters))
            {
                world.BuildStaticGrid(32);
                world.LoadMovers(PerfScenarioCatalog.Parts2048Heartbeat, result);
                Assert.AreEqual(0, result.FailedMods.Count, string.Join("; ", result.FailedMods));
                Assert.AreEqual(PerfScenarioCatalog.HeartbeatHandlerCount, result.LoadedMods.Count);

                RbxInstance mover = registry.WorldRoot.FindFirstChild(PerfLuaSources.MoverNamePrefix + "0_1");
                Assert.IsNotNull(mover, "the first mover of handler 0 must exist under workspace");
                float before = sink.GetPartPropertiesOrDefaultForTests(mover.Id);
                PerfCounterSnapshot start = counters.Snapshot();
                for (int frame = 0; frame < 3; frame++)
                {
                    world.AdvanceFrame(1d / 60d);
                }

                PerfCounterSnapshot ran = counters.Snapshot().Since(start);
                Assert.AreEqual(3 * PerfScenarioCatalog.HeartbeatHandlerCount, ran.ThreadResumes,
                    "every handler runs once per frame");
                Assert.Greater(ran.GuardedSteps, 0L, "the guard counts the handlers' instructions");
                Assert.AreNotEqual(before, sink.GetPartPropertiesOrDefaultForTests(mover.Id),
                    "the Heartbeat handler must move its Part");
                Assert.AreEqual(0, world.Bindings.Scheduler.LiveThreadCount,
                    "a handler that returns leaves no live thread");
            }
        }

        [Test]
        public void RunnerOptions_CommandLineWinsOverEnvironment_AndUnknownFramesFallBack()
        {
            Dictionary<string, string> environment = new()
            {
                ["COREAI_PERF_SCENARIOS"] = "idle-world",
                ["COREAI_PERF_JSON"] = "env.json",
                ["COREAI_PERF_FRAMES"] = "300",
                ["COREAI_PERF_WARMUP"] = "not-a-number",
                ["COREAI_PERF_COMMIT"] = "abc123"
            };
            PerfRunnerOptions options = PerfRunnerOptions.Read(
                new[] { "Player.exe", "-batchmode", "-coreaiPerf", "parts-2048-static", "-coreaiPerfOut", "cli.json",
                    "-coreaiPerfQuit" },
                key => environment.TryGetValue(key, out string value) ? value : null,
                null);

            Assert.IsTrue(options.Requested);
            Assert.AreEqual("parts-2048-static", options.Scenarios);
            Assert.AreEqual("cli.json", options.OutputPath);
            Assert.AreEqual(300, options.MeasuredFrames);
            Assert.AreEqual(PerfRunnerOptions.DefaultWarmupFrames, options.WarmupFrames);
            Assert.AreEqual("abc123", options.Commit);
            Assert.IsTrue(options.QuitWhenDone);
        }

        [Test]
        public void RunnerOptions_WebGlQuery_RequestsARun_ButCannotChooseAFilePath()
        {
            PerfRunnerOptions options = PerfRunnerOptions.Read(null, null,
                "https://example.test/build/index.html?coreaiPerf=idle-world,parts-2048-static&coreaiPerfFrames=240"
                + "&coreaiPerfLabel=webgl+release&coreaiPerfOut=C:/x.json#top");

            Assert.IsTrue(options.Requested);
            Assert.AreEqual("idle-world,parts-2048-static", options.Scenarios);
            Assert.AreEqual(240, options.MeasuredFrames);
            Assert.AreEqual("webgl release", options.Label);
            Assert.AreEqual("", options.OutputPath, "a page URL must not pick a file path");
        }

        [Test]
        public void RunnerOptions_NothingAsked_IsNotRequested()
        {
            PerfRunnerOptions options = PerfRunnerOptions.Read(new[] { "Player.exe" }, key => null, "https://x.test/");
            Assert.IsFalse(options.Requested);
            Assert.AreEqual("all", options.Scenarios);
            Assert.AreEqual(PerfRunnerOptions.DefaultMeasuredFrames, options.MeasuredFrames);
        }

        private static int Count(string text, char c)
        {
            int count = 0;
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] == c)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Reads a Part's Y position from the in-memory sink.</summary>
    internal static class InMemoryPartPropertySinkTestExtensions
    {
        public static float GetPartPropertiesOrDefaultForTests(this InMemoryPartPropertySink sink, InstanceId id)
        {
            return sink.TryGetPartProperties(id, out PartProperties properties) ? properties.Position.Y : float.NaN;
        }
    }
}
