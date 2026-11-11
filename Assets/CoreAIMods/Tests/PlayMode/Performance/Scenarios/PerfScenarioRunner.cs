using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CoreAI.Infrastructure.Lua;
using UnityEngine;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// Builds, runs and measures reference scenes as coroutines, in the Editor (the [Explicit] PlayMode
    /// fixtures) and in a player (<see cref="CoreAiPerfScenarioRunner"/>), so both produce the same rows.
    /// </summary>
    public static class PerfScenarioRunner
    {
        /// <summary>Frames a freshly built scene runs before its warm-up (materialization, first JIT, first upload).</summary>
        public const int SettleFrames = 30;

        /// <summary>
        /// Builds <paramref name="scenario"/>, runs its warm-up and measured frames, tears it down and hands the
        /// result to <paramref name="completed"/>. A scene that fails to build is reported through
        /// <see cref="PerfScenarioResult.Failure"/>, not thrown, so a run of several scenes always finishes.
        /// </summary>
        public static IEnumerator RunScenario(PerfScenario scenario, PerfRunnerOptions options, string harness,
            Action<PerfScenarioResult> completed)
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            options ??= new PerfRunnerOptions();
            PerfScenarioResult result = new(scenario, harness)
            {
                WarmupFrames = options.WarmupFrames,
                MeasuredFrames = options.MeasuredFrames,
                ErrorLogs = 0
            };
            int errorLogs = 0;
            string firstError = null;
            Application.LogCallback onLog = (message, stackTrace, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    errorLogs++;
                    firstError ??= message;
                }
            };
            Application.logMessageReceived += onLog;
            int previousTargetFrameRate = Application.targetFrameRate;
            int previousVSync = QualitySettings.vSyncCount;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            PerfRuntimeCounters counters = new();
            PerfUnityWorld world = null;
            PerfFrameSampler sampler = null;
            try
            {
                float buildStart = Time.realtimeSinceStartup;
                try
                {
                    world = new PerfUnityWorld(counters);
                    world.World.BuildStaticGrid(scenario.StaticPartCount);
                    world.World.LoadMovers(scenario, result);
                    if (scenario.LoadsBundledSampleMods)
                    {
                        world.World.LoadMods(ReadBundledSamples(), result);
                    }
                }
                catch (Exception exception)
                {
                    result.Failure = "build failed: " + exception.GetType().Name + ": " + exception.Message;
                }

                result.Notes.Add("build seconds (grid, mods, materialization): "
                                 + (Time.realtimeSinceStartup - buildStart).ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                if (world != null && result.Failure.Length == 0)
                {
                    for (int frame = 0; frame < SettleFrames + options.WarmupFrames; frame++)
                    {
                        yield return null;
                    }

                    sampler = new PerfFrameSampler();
                    yield return null;
                    PerfCounterSnapshot before = counters.Snapshot();
                    world.Driver.Record(true);
                    sampler.Begin();
                    float measuredStart = Time.realtimeSinceStartup;
                    for (int frame = 0; frame < options.MeasuredFrames; frame++)
                    {
                        yield return null;
                        sampler.Sample();
                    }

                    result.MeasuredSeconds = Time.realtimeSinceStartup - measuredStart;
                    world.Driver.Record(false);
                    result.Lua = counters.Snapshot().Since(before);
                    sampler.End(result);
                    result.SchedulerMilliseconds = PerfDistribution.From(world.Driver.SchedulerMilliseconds);
                    result.Notes.Add("bound GameObjects: " + world.BoundObjects);
                    result.Notes.Add("scheduler live threads at end: " + world.World.Bindings.Scheduler.LiveThreadCount);
                    if (world.Driver.Faults > 0)
                    {
                        result.Notes.Add("frames whose Advance threw: " + world.Driver.Faults + "; first: "
                                         + world.Driver.FirstFault.GetType().Name + ": " + world.Driver.FirstFault.Message);
                    }

                    if (world.World.Warnings.Count > 0)
                    {
                        result.Notes.Add("first Rbx warning: " + world.World.Warnings[0]);
                    }
                }
            }
            finally
            {
                sampler?.Dispose();
                world?.Dispose();
                Application.targetFrameRate = previousTargetFrameRate;
                QualitySettings.vSyncCount = previousVSync;
                Application.logMessageReceived -= onLog;
            }

            result.ErrorLogs = errorLogs;
            if (firstError != null)
            {
                result.Notes.Add("first error log: " + firstError);
            }

            result.Notes.Add("frameMs is Time.unscaledDeltaTime (vSync off, targetFrameRate -1); schedulerMs is ModScheduler.Advance only");
            result.Notes.Add(Application.isEditor
                ? "Editor PlayMode: frame time and garbage include the Editor's own per-frame work"
                : "player build");
            for (int frame = 0; frame < 5; frame++)
            {
                yield return null;
            }

            yield return Resources.UnloadUnusedAssets();
            GC.Collect();
            completed?.Invoke(result);
        }

        /// <summary>Runs every scene of <paramref name="options"/> in order and hands back the whole report.</summary>
        public static IEnumerator RunAll(PerfRunnerOptions options, string harness, Action<PerfRunReport> completed)
        {
            options ??= new PerfRunnerOptions();
            PerfRunReport report = new();
            PerfUnityEnvironment.Describe(report, options);
            IReadOnlyList<PerfScenario> scenarios;
            try
            {
                scenarios = PerfScenarioCatalog.ParseList(options.Scenarios);
            }
            catch (ArgumentException exception)
            {
                report.SetEnvironment("error", exception.Message);
                completed?.Invoke(report);
                yield break;
            }

            for (int index = 0; index < scenarios.Count; index++)
            {
                PerfScenarioResult result = null;
                yield return RunScenario(scenarios[index], options, harness, measured => result = measured);
                if (result != null)
                {
                    report.Scenarios.Add(result);
                }
            }

            completed?.Invoke(report);
        }

        /// <summary>
        /// Writes <paramref name="report"/> to <paramref name="path"/> when one is given and the platform has
        /// a file system, and always prints it to the log between the markers. Returns the path written, or
        /// an empty string.
        /// </summary>
        public static string Publish(PerfRunReport report, string path)
        {
            string written = "";
            if (!string.IsNullOrWhiteSpace(path) && Application.platform != RuntimePlatform.WebGLPlayer)
            {
                try
                {
                    string directory = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(path, PerfReportJson.Write(report), new UTF8Encoding(false));
                    written = Path.GetFullPath(path);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[CoreAI.Perf] could not write the report to '" + path + "': " + exception.Message);
                }
            }

            Debug.Log(PerfReportJson.WriteForLog(report));
            return written;
        }

        /// <summary>
        /// The top-level bundled sample mods (<c>Resources/CoreAIMods/*.lua</c>) as (id, source), through the same
        /// source the production seeder uses; <c>tools/perfbench</c> reads the same files.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> ReadBundledSamples()
        {
            // WHY top-level only: Resources.LoadAll is recursive, so the source also returns the demo mods of the
            // subfolders (Resources/CoreAIMods/BlockWorld), which this scenario never measured and whose HUD mod
            // fails in a world without a UI host. A top-level asset is the one Resources.Load finds by folder and name.
            string folder = ResourcesBundledModSource.DefaultResourceFolder;
            HashSet<string> topLevel = new(StringComparer.Ordinal);
            foreach (TextAsset asset in Resources.LoadAll<TextAsset>(folder))
            {
                if (asset != null && Resources.Load<TextAsset>(folder + "/" + asset.name) == asset)
                {
                    topLevel.Add(asset.text ?? "");
                }
            }

            IReadOnlyList<BundledMod> bundled = new ResourcesBundledModSource(folder).Load();
            List<KeyValuePair<string, string>> mods = new(bundled.Count);
            for (int index = 0; index < bundled.Count; index++)
            {
                if (topLevel.Contains(bundled[index].Source ?? ""))
                {
                    mods.Add(new KeyValuePair<string, string>(bundled[index].Id, bundled[index].Source));
                }
            }

            return mods;
        }
    }
}
