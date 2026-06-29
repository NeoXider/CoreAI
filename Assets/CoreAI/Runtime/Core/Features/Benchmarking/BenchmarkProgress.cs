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

        /// <summary>Fraction 0..1 for a progress bar.</summary>
        public static float Fraction => Total <= 0 ? 0f : (float)Completed / Total;

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
            }
        }

        /// <summary>Marks the scenario currently executing (e.g. "G2 · Score win condition (run 1/1)").</summary>
        public static void StartScenario(string label)
        {
            lock (Gate)
            {
                CurrentLabel = label ?? "";
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
