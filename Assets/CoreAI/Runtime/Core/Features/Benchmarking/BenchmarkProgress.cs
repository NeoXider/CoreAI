using System;
using System.Collections.Generic;

namespace CoreAI.Benchmarking
{
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

    /// <summary>Point-in-time view of one target's run, for editor tooling.</summary>
    public readonly struct BenchmarkRunSnapshot
    {
        public BenchmarkRunSnapshot(string displayName, bool isActive, int total, int completed, string currentLabel,
            bool hasScenarioClock, float scenarioElapsedSeconds, float scenarioRemainingSeconds, string lastLine,
            bool stopRequested)
        {
            DisplayName = displayName ?? "";
            IsActive = isActive;
            Total = total;
            Completed = completed;
            CurrentLabel = currentLabel ?? "";
            HasScenarioClock = hasScenarioClock;
            ScenarioElapsedSeconds = scenarioElapsedSeconds;
            ScenarioRemainingSeconds = scenarioRemainingSeconds;
            LastLine = lastLine ?? "";
            StopRequested = stopRequested;
        }

        public string DisplayName { get; }
        public bool IsActive { get; }
        public int Total { get; }
        public int Completed { get; }
        public string CurrentLabel { get; }
        public bool HasScenarioClock { get; }
        public float ScenarioElapsedSeconds { get; }
        public float ScenarioRemainingSeconds { get; }
        public string LastLine { get; }
        public bool StopRequested { get; }

        /// <summary>Fraction 0..1 of this run's planned scenario executions that finished.</summary>
        public float Fraction => Total <= 0 ? 0f : Math.Min(1f, (float)Completed / Total);
    }

    /// <summary>
    /// Progress handle of one target's run inside a benchmark session. The suite writes through it; the static
    /// <see cref="BenchmarkProgress"/> aggregates every handle for editor tooling.
    /// </summary>
    public sealed class BenchmarkRunProgress
    {
        private readonly object _gate;
        private readonly string _linePrefix;
        private readonly List<ProgressLine> _lines = new();
        private DateTime? _scenarioStartedUtc;
        private float _scenarioTimeoutSeconds;
        private bool _stopRequested;
        private string _currentLabel = "Starting…";
        private int _completed;
        private bool _ended;

        internal BenchmarkRunProgress(object gate, string displayName, int total, string linePrefix)
        {
            _gate = gate;
            DisplayName = displayName ?? "";
            Total = total < 0 ? 0 : total;
            _linePrefix = linePrefix ?? "";
        }

        /// <summary>The target name shown in progress lines.</summary>
        public string DisplayName { get; }

        /// <summary>Planned scenario executions of this run.</summary>
        public int Total { get; }

        /// <summary>
        /// True when this run, or the whole session, was asked to stop. The suite checks it between scenario reps
        /// and at retry boundaries, then writes this run's partial report.
        /// </summary>
        public bool StopRequested
        {
            get
            {
                lock (_gate)
                {
                    return _stopRequested || BenchmarkProgress.SessionStopRequestedLocked;
                }
            }
        }

        /// <summary>Stops only this run; the other targets keep going.</summary>
        public void RequestStop()
        {
            lock (_gate)
            {
                RequestStopLocked();
            }
        }

        internal void RequestStopLocked()
        {
            _stopRequested = true;
        }

        /// <summary>Marks the scenario currently executing; <paramref name="timeoutSeconds"/> powers the scenario clock.</summary>
        public void StartScenario(string label, float timeoutSeconds = 0f)
        {
            lock (_gate)
            {
                _currentLabel = label ?? "";
                _scenarioStartedUtc = DateTime.UtcNow;
                _scenarioTimeoutSeconds = timeoutSeconds;
            }
        }

        /// <summary>Shows what the run is doing between scenarios (for example waiting for the visual lane).</summary>
        public void SetStatus(string label)
        {
            lock (_gate)
            {
                _currentLabel = label ?? "";
                _scenarioStartedUtc = null;
                _scenarioTimeoutSeconds = 0f;
            }
        }

        /// <summary>Records a finished scenario line plus an optional right-aligned tag.</summary>
        public void CompleteScenario(string left, string right = "")
        {
            lock (_gate)
            {
                _completed++;
                if (string.IsNullOrEmpty(left))
                {
                    return;
                }

                ProgressLine line = new(left, right);
                _lines.Add(line);
                BenchmarkProgress.AppendLineLocked(new ProgressLine(_linePrefix + left, right));
            }
        }

        /// <summary>Marks the run finished; the session stays active until every run ended.</summary>
        public void End()
        {
            lock (_gate)
            {
                EndLocked();
            }
        }

        internal void EndLocked()
        {
            _ended = true;
            _currentLabel = "";
            _scenarioStartedUtc = null;
            _scenarioTimeoutSeconds = 0f;
        }

        internal bool IsActiveLocked => !_ended;
        internal int CompletedLocked => _completed;
        internal string CurrentLabelLocked => _currentLabel;
        internal bool HasScenarioClockLocked => _scenarioStartedUtc.HasValue && _scenarioTimeoutSeconds > 0f;

        internal float ScenarioElapsedSecondsLocked => _scenarioStartedUtc.HasValue
            ? (float)(DateTime.UtcNow - _scenarioStartedUtc.Value).TotalSeconds
            : 0f;

        internal float ScenarioTimeoutSecondsLocked => _scenarioTimeoutSeconds;

        internal BenchmarkRunSnapshot SnapshotLocked()
        {
            float elapsed = ScenarioElapsedSecondsLocked;
            return new BenchmarkRunSnapshot(DisplayName, !_ended, Total, _completed, _currentLabel,
                HasScenarioClockLocked, elapsed, Math.Max(0f, _scenarioTimeoutSeconds - elapsed),
                _lines.Count > 0 ? _lines[_lines.Count - 1].Left : "", _stopRequested);
        }
    }

    /// <summary>
    /// Lightweight in-memory progress channel for a running benchmark, so editor tooling (the benchmark window)
    /// can show live progress while the PlayMode suite runs. No I/O, no Unity types.
    /// <para>
    /// A session holds one <see cref="BenchmarkRunProgress"/> per target. The static members aggregate them: with a
    /// single run they read exactly as the single-model API always did; with several they sum the counts and
    /// <see cref="Runs"/> lists every target. <see cref="Begin"/>/<see cref="StartScenario"/>/
    /// <see cref="CompleteScenario"/>/<see cref="End"/> remain as the single-run shortcut.
    /// </para>
    /// </summary>
    public static class BenchmarkProgress
    {
        private const int MaxAggregateLines = 400;

        private static readonly object Gate = new();
        private static readonly List<ProgressLine> CompletedLines = new();
        private static readonly List<BenchmarkRunProgress> ActiveRuns = new();
        private static BenchmarkRunProgress _legacyRun;
        private static bool _sessionActive;
        private static bool _sessionStopRequested;

        /// <summary>True while a benchmark session is running (from its begin until its end).</summary>
        public static bool IsRunning
        {
            get
            {
                lock (Gate)
                {
                    return _sessionActive;
                }
            }
        }

        /// <summary>Planned scenario executions summed over every run of the session.</summary>
        public static int Total
        {
            get
            {
                lock (Gate)
                {
                    int total = 0;
                    foreach (BenchmarkRunProgress run in ActiveRuns)
                    {
                        total += run.Total;
                    }

                    return total;
                }
            }
        }

        /// <summary>Finished scenario executions summed over every run of the session.</summary>
        public static int Completed
        {
            get
            {
                lock (Gate)
                {
                    int completed = 0;
                    foreach (BenchmarkRunProgress run in ActiveRuns)
                    {
                        completed += run.CompletedLocked;
                    }

                    return completed;
                }
            }
        }

        /// <summary>The single run's model, or "N targets" for a multi-target session.</summary>
        public static string ModelId
        {
            get
            {
                lock (Gate)
                {
                    return ActiveRuns.Count switch
                    {
                        0 => "",
                        1 => ActiveRuns[0].DisplayName,
                        _ => $"{ActiveRuns.Count} targets"
                    };
                }
            }
        }

        /// <summary>The single run's current scenario, or how many targets are still running.</summary>
        public static string CurrentLabel
        {
            get
            {
                lock (Gate)
                {
                    if (ActiveRuns.Count == 1)
                    {
                        return ActiveRuns[0].CurrentLabelLocked;
                    }

                    int active = 0;
                    foreach (BenchmarkRunProgress run in ActiveRuns)
                    {
                        active += run.IsActiveLocked ? 1 : 0;
                    }

                    return ActiveRuns.Count == 0 ? "" : $"{active} of {ActiveRuns.Count} targets running";
                }
            }
        }

        /// <summary>Fraction 0..1 for a progress bar.</summary>
        public static float Fraction
        {
            get
            {
                int total = Total;
                return total <= 0 ? 0f : Math.Min(1f, (float)Completed / total);
            }
        }

        /// <summary>
        /// Cooperative stop flag for the whole session. A Stop button sets it via <see cref="RequestStop"/>; every
        /// run checks it between scenario reps and, when set, breaks GRACEFULLY — the report for everything finished
        /// so far is still written (same path as the soft time-budget stop).
        /// </summary>
        public static bool StopRequested
        {
            get
            {
                lock (Gate)
                {
                    return _sessionStopRequested;
                }
            }
        }

        internal static bool SessionStopRequestedLocked => _sessionStopRequested;

        /// <summary>Requests a graceful stop of every run of the session (partial reports are still saved).</summary>
        public static void RequestStop()
        {
            lock (Gate)
            {
                _sessionStopRequested = true;
            }
        }

        /// <summary>Stops only the run named <paramref name="displayName"/>; returns false when no such run is active.</summary>
        public static bool RequestStop(string displayName)
        {
            lock (Gate)
            {
                foreach (BenchmarkRunProgress run in ActiveRuns)
                {
                    if (run.IsActiveLocked && string.Equals(run.DisplayName, displayName, StringComparison.Ordinal))
                    {
                        run.RequestStopLocked();
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// True when exactly one run is in the session and its scenario clock is known — a single long-running
        /// scenario (e.g. G6's free build alone) sits at a meaningless 0% count-based fraction otherwise.
        /// </summary>
        public static bool HasScenarioClock
        {
            get
            {
                lock (Gate)
                {
                    return ActiveRuns.Count == 1 && ActiveRuns[0].HasScenarioClockLocked;
                }
            }
        }

        /// <summary>Seconds elapsed since the single run's current scenario started, or 0.</summary>
        public static float ScenarioElapsedSeconds
        {
            get
            {
                lock (Gate)
                {
                    return ActiveRuns.Count == 1 ? ActiveRuns[0].ScenarioElapsedSecondsLocked : 0f;
                }
            }
        }

        /// <summary>Seconds left before the single run's current scenario times out, clamped to 0.</summary>
        public static float ScenarioRemainingSeconds
        {
            get
            {
                lock (Gate)
                {
                    return ActiveRuns.Count == 1
                        ? Math.Max(0f, ActiveRuns[0].ScenarioTimeoutSecondsLocked -
                                       ActiveRuns[0].ScenarioElapsedSecondsLocked)
                        : 0f;
                }
            }
        }

        /// <summary>Fraction 0..1 of the single run's current scenario timeout elapsed.</summary>
        public static float ScenarioTimeFraction
        {
            get
            {
                lock (Gate)
                {
                    if (ActiveRuns.Count != 1 || ActiveRuns[0].ScenarioTimeoutSecondsLocked <= 0f)
                    {
                        return 0f;
                    }

                    return Math.Min(1f, Math.Max(0f,
                        ActiveRuns[0].ScenarioElapsedSecondsLocked / ActiveRuns[0].ScenarioTimeoutSecondsLocked));
                }
            }
        }

        /// <summary>Every run of the current (or last) session, in the order they were begun.</summary>
        public static IReadOnlyList<BenchmarkRunSnapshot> Runs()
        {
            lock (Gate)
            {
                List<BenchmarkRunSnapshot> list = new(ActiveRuns.Count);
                foreach (BenchmarkRunProgress run in ActiveRuns)
                {
                    list.Add(run.SnapshotLocked());
                }

                return list;
            }
        }

        /// <summary>Starts a session: clears the previous runs, lines and stop flag.</summary>
        public static void BeginSession()
        {
            lock (Gate)
            {
                _sessionActive = true;
                _sessionStopRequested = false;
                ActiveRuns.Clear();
                CompletedLines.Clear();
                _legacyRun = null;
            }
        }

        /// <summary>
        /// Registers one target's run in the current session and returns its handle. When the session holds more
        /// than one run, its finished lines are prefixed with <paramref name="displayName"/> in the shared list.
        /// </summary>
        public static BenchmarkRunProgress BeginRun(string displayName, int total, bool prefixLines)
        {
            lock (Gate)
            {
                if (!_sessionActive)
                {
                    _sessionActive = true;
                    _sessionStopRequested = false;
                    ActiveRuns.Clear();
                    CompletedLines.Clear();
                }

                BenchmarkRunProgress run = new(Gate, displayName, total,
                    prefixLines && !string.IsNullOrEmpty(displayName) ? displayName + " · " : "");
                ActiveRuns.Add(run);
                return run;
            }
        }

        /// <summary>Ends the session; the runs stay readable through <see cref="Runs"/> until the next session.</summary>
        public static void EndSession()
        {
            lock (Gate)
            {
                foreach (BenchmarkRunProgress run in ActiveRuns)
                {
                    run.EndLocked();
                }

                _sessionActive = false;
            }
        }

        /// <summary>Single-run shortcut: starts a session with one run of <paramref name="total"/> executions.</summary>
        public static void Begin(int total, string modelId)
        {
            BeginSession();
            BenchmarkRunProgress run = BeginRun(modelId, total, false);
            lock (Gate)
            {
                _legacyRun = run;
            }
        }

        /// <summary>Single-run shortcut for <see cref="BenchmarkRunProgress.StartScenario"/>.</summary>
        public static void StartScenario(string label, float timeoutSeconds = 0f)
        {
            LegacyRun()?.StartScenario(label, timeoutSeconds);
        }

        /// <summary>Single-run shortcut for <see cref="BenchmarkRunProgress.CompleteScenario"/>.</summary>
        public static void CompleteScenario(string left, string right = "")
        {
            LegacyRun()?.CompleteScenario(left, right);
        }

        /// <summary>Single-run shortcut that ends the session.</summary>
        public static void End()
        {
            EndSession();
        }

        /// <summary>Snapshot of the most recent completed lines of the session (newest last), capped to <paramref name="max"/>.</summary>
        public static IReadOnlyList<ProgressLine> RecentLines(int max = 12)
        {
            lock (Gate)
            {
                int start = CompletedLines.Count > max ? CompletedLines.Count - max : 0;
                return CompletedLines.GetRange(start, CompletedLines.Count - start);
            }
        }

        internal static void AppendLineLocked(ProgressLine line)
        {
            CompletedLines.Add(line);
            if (CompletedLines.Count > MaxAggregateLines)
            {
                CompletedLines.RemoveRange(0, CompletedLines.Count - MaxAggregateLines);
            }
        }

        private static BenchmarkRunProgress LegacyRun()
        {
            lock (Gate)
            {
                return _legacyRun;
            }
        }
    }
}
