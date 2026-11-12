using System;
using System.Collections.Generic;

namespace CoreAI.Diagnostics
{
    /// <summary>Frame rate over a recent window, computed from recorded frame intervals.</summary>
    public readonly struct FrameRateSnapshot
    {
        /// <param name="frames">Frames in the window.</param>
        /// <param name="windowSeconds">Sum of their intervals (the time the window really covers).</param>
        /// <param name="averageFps">Frames divided by the covered time.</param>
        /// <param name="minimumFps">One frame over the longest interval: the worst moment of the window.</param>
        /// <param name="averageFrameMilliseconds">Mean interval.</param>
        /// <param name="p95FrameMilliseconds">95th percentile interval (nearest rank).</param>
        /// <param name="maxFrameMilliseconds">Longest interval.</param>
        /// <param name="slowFrames">Frames longer than 50 ms (under 20 fps).</param>
        public FrameRateSnapshot(int frames, double windowSeconds, double averageFps, double minimumFps,
            double averageFrameMilliseconds, double p95FrameMilliseconds, double maxFrameMilliseconds, int slowFrames)
        {
            Frames = frames;
            WindowSeconds = windowSeconds;
            AverageFps = averageFps;
            MinimumFps = minimumFps;
            AverageFrameMilliseconds = averageFrameMilliseconds;
            P95FrameMilliseconds = p95FrameMilliseconds;
            MaxFrameMilliseconds = maxFrameMilliseconds;
            SlowFrames = slowFrames;
        }

        /// <summary>Frames in the window; 0 when nothing was recorded (edit mode, before the first frame).</summary>
        public int Frames { get; }

        /// <summary>The time the window really covers, in seconds.</summary>
        public double WindowSeconds { get; }

        /// <summary>Frames per second over the window.</summary>
        public double AverageFps { get; }

        /// <summary>Frames per second of the longest frame.</summary>
        public double MinimumFps { get; }

        /// <summary>Mean frame interval in milliseconds.</summary>
        public double AverageFrameMilliseconds { get; }

        /// <summary>95th percentile frame interval in milliseconds.</summary>
        public double P95FrameMilliseconds { get; }

        /// <summary>Longest frame interval in milliseconds.</summary>
        public double MaxFrameMilliseconds { get; }

        /// <summary>Frames longer than <see cref="FrameTimeWindow.SlowFrameMilliseconds"/>.</summary>
        public int SlowFrames { get; }
    }

    /// <summary>One engine counter reading (draw calls, memory) with its unit.</summary>
    public readonly struct EngineCounterValue
    {
        /// <param name="name">Stable snake_case name, for example <c>draw_calls</c>.</param>
        /// <param name="value">The reading.</param>
        /// <param name="unit"><c>count</c>, <c>bytes</c> or <c>ms</c>.</param>
        public EngineCounterValue(string name, double value, string unit)
        {
            Name = name ?? "";
            Value = value;
            Unit = unit ?? "";
        }

        /// <summary>Stable snake_case name.</summary>
        public string Name { get; }

        /// <summary>The reading.</summary>
        public double Value { get; }

        /// <summary><c>count</c>, <c>bytes</c> or <c>ms</c>.</summary>
        public string Unit { get; }
    }

    /// <summary>
    /// The engine counters a build really provides at this moment, and the names of those it does not. A counter
    /// that is missing is listed in <see cref="Unavailable"/>, never reported as zero.
    /// </summary>
    public sealed class EngineCounterSnapshot
    {
        /// <param name="values">Counters that returned a reading.</param>
        /// <param name="unavailable">Counters this build or platform does not provide (or has not sampled yet).</param>
        /// <param name="note">Free text about how the readings were taken; may be empty.</param>
        public EngineCounterSnapshot(IReadOnlyList<EngineCounterValue> values, IReadOnlyList<string> unavailable,
            string note = "")
        {
            Values = values ?? Array.Empty<EngineCounterValue>();
            Unavailable = unavailable ?? Array.Empty<string>();
            Note = note ?? "";
        }

        /// <summary>An empty snapshot for hosts without engine counters.</summary>
        public static EngineCounterSnapshot None { get; } = new(null, null, "this host reports no engine counters");

        /// <summary>Counters that returned a reading.</summary>
        public IReadOnlyList<EngineCounterValue> Values { get; }

        /// <summary>Counters this build does not provide.</summary>
        public IReadOnlyList<string> Unavailable { get; }

        /// <summary>How the readings were taken; may be empty.</summary>
        public string Note { get; }
    }

    /// <summary>
    /// Read side of the runtime performance monitor: the frame rate over a recent window and the engine
    /// counters the running build provides. Read by debug surfaces (the MCP <c>get_performance</c> and
    /// <c>game_status</c> tools, the in-game <c>get_mod_logs</c> <c>source=unity</c> mode). Call on the main thread.
    /// </summary>
    public interface IRuntimePerformanceSource
    {
        /// <summary>Frame rate over the last <paramref name="windowSeconds"/> seconds (clamped to what is retained).</summary>
        FrameRateSnapshot GetFrameRate(double windowSeconds);

        /// <summary>The engine counters available now; the first call may start them and report them unavailable.</summary>
        EngineCounterSnapshot GetCounters();
    }

    /// <summary>
    /// Fixed-size ring of frame intervals with the time each was recorded. Engine-free: a host records one
    /// interval per frame (Unity: <c>Time.unscaledDeltaTime</c>) and reads a <see cref="FrameRateSnapshot"/> over
    /// any window up to what the ring retains. Not thread-safe; record and read on one thread.
    /// </summary>
    public sealed class FrameTimeWindow
    {
        /// <summary>Default retained frames: one minute at 120 fps.</summary>
        public const int DefaultCapacity = 7200;

        /// <summary>A frame longer than this counts as slow.</summary>
        public const double SlowFrameMilliseconds = 50d;

        private readonly double[] _recordedAt;
        private readonly float[] _milliseconds;
        private int _next;
        private int _count;

        /// <param name="capacity">Retained frames.</param>
        public FrameTimeWindow(int capacity = DefaultCapacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _recordedAt = new double[capacity];
            _milliseconds = new float[capacity];
        }

        /// <summary>Retained frames.</summary>
        public int Count => _count;

        /// <summary>Records one frame interval measured at <paramref name="nowSeconds"/> (a monotonic clock).</summary>
        public void Record(double nowSeconds, double frameMilliseconds)
        {
            if (double.IsNaN(frameMilliseconds) || frameMilliseconds <= 0d)
            {
                return;
            }

            _recordedAt[_next] = nowSeconds;
            _milliseconds[_next] = (float)frameMilliseconds;
            _next = (_next + 1) % _milliseconds.Length;
            if (_count < _milliseconds.Length) _count++;
        }

        /// <summary>Forgets every recorded frame.</summary>
        public void Clear()
        {
            _next = 0;
            _count = 0;
        }

        /// <summary>
        /// The frames recorded in the last <paramref name="windowSeconds"/> before <paramref name="nowSeconds"/>; a
        /// window of 0 or less covers everything retained.
        /// </summary>
        public FrameRateSnapshot Snapshot(double nowSeconds, double windowSeconds)
        {
            double from = windowSeconds > 0d ? nowSeconds - windowSeconds : double.NegativeInfinity;
            List<float> frames = new(Math.Min(_count, 1024));
            double total = 0d;
            double max = 0d;
            int slow = 0;
            for (int offset = 1; offset <= _count; offset++)
            {
                int index = (_next - offset + _milliseconds.Length) % _milliseconds.Length;
                if (_recordedAt[index] < from)
                {
                    break;
                }

                float ms = _milliseconds[index];
                frames.Add(ms);
                total += ms;
                if (ms > max) max = ms;
                if (ms > SlowFrameMilliseconds) slow++;
            }

            if (frames.Count == 0 || total <= 0d)
            {
                return default;
            }

            frames.Sort();
            int rank = (int)Math.Ceiling(0.95 * frames.Count) - 1;
            double p95 = frames[Math.Max(0, Math.Min(frames.Count - 1, rank))];
            return new FrameRateSnapshot(frames.Count, total / 1000d, frames.Count * 1000d / total, 1000d / max,
                total / frames.Count, p95, max, slow);
        }
    }
}
