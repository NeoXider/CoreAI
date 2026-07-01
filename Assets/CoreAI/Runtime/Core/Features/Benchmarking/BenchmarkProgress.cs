using System;
using System.Collections.Generic;

namespace CoreAI.Benchmarking
{
    /// <summary>
    /// Lightweight in-memory progress channel for a running benchmark, so editor tooling (the benchmark
    /// window) can show live progress while the PlayMode suite runs. The suite (which references the core)
    /// pushes updates; the editor window (also referencing the core) polls them. No I/O, no Unity types —
    /// just a shared snapshot updated on the main thread.
    /// </summary>
    /// <summary>One finished line for the live progress list: a left text and an optional right tag.</summary>
    public readonly struct ProgressLine
    {
        public ProgressLine(string left, string right)
        {
            Left = left ?? "";
            Right = right ?? "";
        }

        public string Left { get; }
        public string Right { get; }
    }

    public static class BenchmarkProgress
    {
        private static readonly object Gate = new();
        private static readonly List<ProgressLine> CompletedLines = new();

        public static bool IsRunning { get; private set; }
        public static int Total { get; private set; }
        public static int Completed { get; private set; }
        public static string ModelId { get; private set; } = "";
        public static string CurrentLabel { get; private set; } = "";
        private static DateTime? _currentScenarioStartedUtc;
        private static float _currentScenarioTimeoutSeconds;

        /// <summary>Fraction 0..1 for a progress bar.</summary>
        public static float Fraction => Total <= 0 ? 0f : (float)Completed / Total;

        /// <summary>
        /// True when the current scenario's elapsed/remaining wall-clock time is known and worth showing -
        /// a single long-running scenario (e.g. G6's free build alone, <see cref="Total"/> == 1) sits at a
        /// meaningless 0% count-based fraction for its whole multi-minute run otherwise.
        /// </summary>
        public static bool HasScenarioClock => _currentScenarioStartedUtc.HasValue && _currentScenarioTimeoutSeconds > 0f;

        /// <summary>Seconds elapsed since the current scenario started, or 0 when no clock is set.</summary>
        public static float ScenarioElapsedSeconds => _currentScenarioStartedUtc.HasValue
            ? (float)(DateTime.UtcNow - _currentScenarioStartedUtc.Value).TotalSeconds
            : 0f;

        /// <summary>Seconds left before the current scenario's own timeout, clamped to 0.</summary>
        public static float ScenarioRemainingSeconds =>
            Math.Max(0f, _currentScenarioTimeoutSeconds - ScenarioElapsedSeconds);

        /// <summary>Fraction 0..1 of the current scenario's own timeout budget elapsed.</summary>
        public static float ScenarioTimeFraction => _currentScenarioTimeoutSeconds > 0f
            ? Math.Min(1f, Math.Max(0f, ScenarioElapsedSeconds / _currentScenarioTimeoutSeconds))
            : 0f;

        /// <summary>Begins a run of <paramref name="total"/> scenario executions (scenarios × repetitions).</summary>
        public static void Begin(int total, string modelId)
        {
            lock (Gate)
            {
                IsRunning = true;
                Total = total < 0 ? 0 : total;
                Completed = 0;
                ModelId = modelId ?? "";
                CurrentLabel = "Starting…";
                CompletedLines.Clear();
                _currentScenarioStartedUtc = null;
                _currentScenarioTimeoutSeconds = 0f;
            }
        }

        /// <summary>Marks the scenario currently executing (e.g. "G2 · Score win condition (run 1/1)").
        /// <paramref name="timeoutSeconds"/> is the scenario's own wall-clock budget, when known
        /// (0/negative = unknown) - it powers <see cref="HasScenarioClock"/> so a solo long-running
        /// scenario (<see cref="Total"/> == 1, e.g. G6 alone) can show elapsed/remaining time instead of a
        /// count-based fraction that would otherwise sit at 0% for the whole run.</summary>
        public static void StartScenario(string label, float timeoutSeconds = 0f)
        {
            lock (Gate)
            {
                CurrentLabel = label ?? "";
                _currentScenarioStartedUtc = DateTime.UtcNow;
                _currentScenarioTimeoutSeconds = timeoutSeconds;
            }
        }

        /// <summary>Records a finished scenario line (left text, e.g. "✅ Flat damage buff — 100") plus an
        /// optional right-aligned tag (e.g. a difficulty indicator).</summary>
        public static void CompleteScenario(string left, string right = "")
        {
            lock (Gate)
            {
                Completed++;
                if (!string.IsNullOrEmpty(left))
                {
                    CompletedLines.Add(new ProgressLine(left, right));
                }
            }
        }

        public static void End()
        {
            lock (Gate)
            {
                IsRunning = false;
                CurrentLabel = "";
                _currentScenarioStartedUtc = null;
                _currentScenarioTimeoutSeconds = 0f;
            }
        }

        /// <summary>Snapshot of the most recent completed lines (newest last), capped to <paramref name="max"/>.</summary>
        public static IReadOnlyList<ProgressLine> RecentLines(int max = 12)
        {
            lock (Gate)
            {
                int start = CompletedLines.Count > max ? CompletedLines.Count - max : 0;
                return CompletedLines.GetRange(start, CompletedLines.Count - start);
            }
        }
    }
}
