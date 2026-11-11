using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// One measured reference scene. Every series that could not be measured on the running platform is
    /// left empty and named in <see cref="Unavailable"/>, so a missing counter is never reported as zero.
    /// </summary>
    public sealed class PerfScenarioResult
    {
        /// <param name="scenario">The scene that was measured.</param>
        /// <param name="harness">Which harness produced the row (for example <c>unity-playmode</c>).</param>
        public PerfScenarioResult(PerfScenario scenario, string harness)
        {
            Scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
            Harness = harness ?? "";
        }

        /// <summary>The scene that was measured.</summary>
        public PerfScenario Scenario { get; }

        /// <summary>Which harness produced the row.</summary>
        public string Harness { get; }

        /// <summary>Frames run before measuring.</summary>
        public int WarmupFrames { get; set; }

        /// <summary>Frames measured.</summary>
        public int MeasuredFrames { get; set; }

        /// <summary>Wall time of each measured frame, milliseconds.</summary>
        public PerfDistribution FrameMilliseconds { get; set; }

        /// <summary>Time spent inside the scheduler frame (Lua, signals, scheduler) per frame, milliseconds.</summary>
        public PerfDistribution SchedulerMilliseconds { get; set; }

        /// <summary>Main-thread CPU time per frame from the engine profiler, milliseconds (empty where unavailable).</summary>
        public PerfDistribution MainThreadMilliseconds { get; set; }

        /// <summary>Managed bytes allocated per frame.</summary>
        public PerfDistribution GcAllocatedBytesPerFrame { get; set; }

        /// <summary>Where <see cref="GcAllocatedBytesPerFrame"/> came from (exact counter, profiler recorder or estimate).</summary>
        public string GcAllocatedSource { get; set; } = "";

        /// <summary>Managed collections that ran during the measured frames, -1 when unknown.</summary>
        public int GcCollections { get; set; } = -1;

        /// <summary>Render batches per frame.</summary>
        public PerfDistribution Batches { get; set; }

        /// <summary>Draw calls per frame.</summary>
        public PerfDistribution DrawCalls { get; set; }

        /// <summary>SetPass calls per frame.</summary>
        public PerfDistribution SetPassCalls { get; set; }

        /// <summary>Managed heap reading after the measured frames, bytes.</summary>
        public long ManagedHeapBytes { get; set; } = -1;

        /// <summary>Named memory readings (bytes) after the measured frames, in the order they were taken.</summary>
        public List<KeyValuePair<string, long>> Memory { get; } = new();

        /// <summary>Lua counters accumulated over the measured frames.</summary>
        public PerfCounterSnapshot Lua { get; set; }

        /// <summary>Seconds the measured frames took, for the per-second rates.</summary>
        public double MeasuredSeconds { get; set; }

        /// <summary>Mod ids that loaded.</summary>
        public List<string> LoadedMods { get; } = new();

        /// <summary>Mod ids that failed to load, each with its error line.</summary>
        public List<string> FailedMods { get; } = new();

        /// <summary>Counters the platform did not provide.</summary>
        public List<string> Unavailable { get; } = new();

        /// <summary>Anything a reader needs to interpret the row.</summary>
        public List<string> Notes { get; } = new();

        /// <summary>Error and exception log messages raised while the scene was built and measured, -1 when not observed.</summary>
        public int ErrorLogs { get; set; } = -1;

        /// <summary>Set when the scene could not be built or measured; the other fields are then incomplete.</summary>
        public string Failure { get; set; } = "";

        /// <summary>Lua instructions per second over the measured frames.</summary>
        public double GuardedStepsPerSecond => MeasuredSeconds > 0d ? Lua.GuardedSteps / MeasuredSeconds : 0d;

        /// <summary>Script-thread resumes per second over the measured frames.</summary>
        public double ResumesPerSecond => MeasuredSeconds > 0d ? Lua.ThreadResumes / MeasuredSeconds : 0d;
    }

    /// <summary>A whole run: where it ran and the scenes it measured.</summary>
    public sealed class PerfRunReport
    {
        /// <summary>Report format identifier; bumped when a field changes meaning.</summary>
        public const string Schema = "coreai-perf/1";

        /// <summary>Named facts about the machine, runtime, build and commit, in insertion order.</summary>
        public List<KeyValuePair<string, string>> Environment { get; } = new();

        /// <summary>The measured scenes in run order.</summary>
        public List<PerfScenarioResult> Scenarios { get; } = new();

        /// <summary>Adds or replaces one environment fact.</summary>
        public void SetEnvironment(string key, string value)
        {
            for (int index = 0; index < Environment.Count; index++)
            {
                if (string.Equals(Environment[index].Key, key, StringComparison.Ordinal))
                {
                    Environment[index] = new KeyValuePair<string, string>(key, value ?? "");
                    return;
                }
            }

            Environment.Add(new KeyValuePair<string, string>(key, value ?? ""));
        }
    }
}
