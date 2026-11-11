using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CoreAI.Ai;
using CoreAI.Mods.Performance;

namespace CoreAI.PerfBench
{
    /// <summary>
    /// Runs the P19 reference scenes headless: the production scheduler frame over an in-memory world, no
    /// GameObjects and no rendering. What this measures is the Lua, signal and scheduler share of a frame and
    /// its exact allocation; the binder and render share needs the Unity harness.
    /// </summary>
    internal static class ScenarioFrames
    {
        public const double FrameDeltaSeconds = 1d / 60d;

        public static PerfScenarioResult Run(PerfScenario scenario, string repoRoot, int warmupFrames,
            int measuredFrames, bool countAllocations, string harness)
        {
            PerfScenarioResult result = new(scenario, harness)
            {
                WarmupFrames = warmupFrames,
                MeasuredFrames = measuredFrames,
                GcAllocatedSource = countAllocations
                    ? "exact: GC.GetAllocatedBytesForCurrentThread around the scheduler frame"
                    : "unavailable"
            };
            using HeadlessWorld world = HeadlessWorld.Create();
            world.World.BuildStaticGrid(scenario.StaticPartCount);
            world.World.LoadMovers(scenario, result);
            if (scenario.LoadsBundledSampleMods)
            {
                world.World.LoadMods(ReadBundledSamples(repoRoot), result);
            }

            for (int frame = 0; frame < warmupFrames; frame++)
            {
                world.World.AdvanceFrame(FrameDeltaSeconds);
            }

            List<double> milliseconds = new(measuredFrames);
            List<double> bytes = new(measuredFrames);
            PerfCounterSnapshot before = world.Counters.Snapshot();
            int collectionsBefore = GC.CollectionCount(0);
            long runStart = Stopwatch.GetTimestamp();
            for (int frame = 0; frame < measuredFrames; frame++)
            {
                long allocatedBefore = countAllocations ? BenchEnvironment.AllocatedBytes() : 0;
                long start = Stopwatch.GetTimestamp();
                world.World.AdvanceFrame(FrameDeltaSeconds);
                long elapsed = Stopwatch.GetTimestamp() - start;
                if (countAllocations)
                {
                    bytes.Add(BenchEnvironment.AllocatedBytes() - allocatedBefore);
                }

                milliseconds.Add(elapsed * 1000d / Stopwatch.Frequency);
            }

            result.MeasuredSeconds = (Stopwatch.GetTimestamp() - runStart) / (double)Stopwatch.Frequency;
            result.GcCollections = GC.CollectionCount(0) - collectionsBefore;
            result.Lua = world.Counters.Snapshot().Since(before);
            result.FrameMilliseconds = PerfDistribution.From(milliseconds);
            result.SchedulerMilliseconds = result.FrameMilliseconds;
            result.GcAllocatedBytesPerFrame = countAllocations ? PerfDistribution.From(bytes) : PerfDistribution.Empty;
            result.ManagedHeapBytes = GC.GetTotalMemory(false);
            result.Unavailable.Add("batches, draw calls, SetPass calls (no renderer in the headless harness)");
            if (!countAllocations)
            {
                result.Unavailable.Add("gcAllocatedBytesPerFrame (this runtime does not count per-thread allocations)");
            }

            result.Notes.Add("headless: in-memory backing binder and part store; frames are back to back, not paced");
            result.Notes.Add("frameMs is the scheduler frame (ModScheduler.Advance) only");
            result.Notes.Add("scheduler live threads at end: " + world.World.Bindings.Scheduler.LiveThreadCount);
            if (world.World.Warnings.Count > 0)
            {
                result.Notes.Add("first Rbx warning: " + world.World.Warnings[0]);
            }

            if (world.World.DroppedWarnings > 0)
            {
                result.Notes.Add("further Rbx warnings dropped: " + world.World.DroppedWarnings);
            }

            return result;
        }

        /// <summary>The bundled sample mods as (id, source), read from the package's Resources folder.</summary>
        public static IReadOnlyList<KeyValuePair<string, string>> ReadBundledSamples(string repoRoot)
        {
            string folder = Path.Combine(repoRoot, "Assets", "CoreAIMods", "Runtime", "Resources", "CoreAIMods");
            string[] files = Directory.GetFiles(folder, "*.lua");
            Array.Sort(files, StringComparer.Ordinal);
            List<KeyValuePair<string, string>> mods = new(files.Length);
            for (int index = 0; index < files.Length; index++)
            {
                string source = File.ReadAllText(files[index]);
                LuaModHeader header = LuaModHeader.Parse(source, Path.GetFileNameWithoutExtension(files[index]));
                string id = string.IsNullOrWhiteSpace(header.Id)
                    ? Path.GetFileNameWithoutExtension(files[index])
                    : header.Id.Trim();
                mods.Add(new KeyValuePair<string, string>(id, source));
            }

            return mods;
        }
    }
}
