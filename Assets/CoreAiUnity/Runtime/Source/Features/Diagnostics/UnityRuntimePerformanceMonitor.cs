using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// Unity side of <see cref="IRuntimePerformanceSource"/>: records <c>Time.unscaledDeltaTime</c> every frame into a
    /// <see cref="FrameTimeWindow"/> (one minute at 120 fps) and reads engine counters through
    /// <see cref="ProfilerRecorder"/>.
    /// <para>
    /// What a build provides is detected when the counters are read, never assumed: a recorder that is not valid,
    /// or has no sample yet, is listed as unavailable instead of reported as zero. Unity documents the render
    /// counters (draw calls, batches, SetPass calls, triangles, vertices) as readable in release players, and the
    /// frame-time counters as starting the frame timing manager themselves, also in release players; WebGL gives no
    /// GPU time. The object counts (textures, meshes) are development-build only and are not read.
    /// </para>
    /// <para>
    /// WHY one process-wide instance (<see cref="Shared"/>): frame timing belongs to the process, and a sampler
    /// owned by a scope would restart at every scene load. Its frame driver is a <c>DontDestroyOnLoad</c> object
    /// created on the first <see cref="EnsureStarted"/> in play mode. Recorders start on the first
    /// <see cref="GetCounters"/> call, because a running recorder has a small per-frame cost.
    /// </para>
    /// </summary>
    public sealed class UnityRuntimePerformanceMonitor : IRuntimePerformanceSource
    {
        private readonly FrameTimeWindow _frames;
        private readonly object _gate = new();
        private CounterReader[] _counters;
        private CoreAiRuntimePerformanceDriver _driver;

        /// <param name="frames">The ring the monitor records into; null creates the default one.</param>
        public UnityRuntimePerformanceMonitor(FrameTimeWindow frames = null)
        {
            _frames = frames ?? new FrameTimeWindow();
        }

        /// <summary>The process-wide monitor.</summary>
        public static UnityRuntimePerformanceMonitor Shared { get; } = new();

        /// <summary>True once the frame driver exists (play mode only).</summary>
        public bool IsSampling => _driver != null;

        /// <summary>Creates the frame driver in play mode; later calls, and calls outside play mode, do nothing.</summary>
        public void EnsureStarted()
        {
            if (_driver != null || !Application.isPlaying)
            {
                return;
            }

            GameObject host = new("CoreAI Performance Monitor");
            UnityEngine.Object.DontDestroyOnLoad(host);
            _driver = host.AddComponent<CoreAiRuntimePerformanceDriver>();
            _driver.Initialize(this);
        }

        /// <summary>Records one frame; called by the driver after every Update.</summary>
        internal void RecordFrame()
        {
            lock (_gate)
            {
                _frames.Record(Time.realtimeSinceStartupAsDouble, Time.unscaledDeltaTime * 1000d);
            }
        }

        internal void OnDriverDestroyed(CoreAiRuntimePerformanceDriver driver)
        {
            if (ReferenceEquals(_driver, driver)) _driver = null;
        }

        /// <inheritdoc />
        public FrameRateSnapshot GetFrameRate(double windowSeconds)
        {
            lock (_gate)
            {
                return _frames.Snapshot(Time.realtimeSinceStartupAsDouble, windowSeconds);
            }
        }

        /// <inheritdoc />
        public EngineCounterSnapshot GetCounters()
        {
            bool justStarted = false;
            if (_counters == null)
            {
                _counters = CounterReader.StartAll();
                justStarted = true;
                Application.quitting += DisposeCounters;
            }

            List<EngineCounterValue> values = new();
            List<string> unavailable = new();
            foreach (CounterReader counter in _counters)
            {
                if (counter.TryRead(out double value))
                {
                    values.Add(new EngineCounterValue(counter.Name, value, counter.Unit));
                }
                else
                {
                    unavailable.Add(counter.Name);
                }
            }

            values.Add(new EngineCounterValue("managed_heap", GC.GetTotalMemory(false), "bytes"));
            string note = justStarted && unavailable.Count > 0
                ? "counters started now; per-frame counters report from the next call"
                : "";
            return new EngineCounterSnapshot(values, unavailable, note);
        }

        private void DisposeCounters()
        {
            Application.quitting -= DisposeCounters;
            CounterReader[] counters = _counters;
            _counters = null;
            if (counters == null) return;
            foreach (CounterReader counter in counters) counter.Dispose();
        }

        private sealed class CounterReader : IDisposable
        {
            private readonly bool _perFrame;
            private readonly double _scale;
            private ProfilerRecorder _recorder;

            private CounterReader(string name, ProfilerCategory category, string statName, string unit, bool perFrame,
                double scale)
            {
                Name = name;
                Unit = unit;
                _perFrame = perFrame;
                _scale = scale;
                _recorder = ProfilerRecorder.StartNew(category, statName, 1);
            }

            public string Name { get; }

            public string Unit { get; }

            public static CounterReader[] StartAll()
            {
                const double NanosecondsToMilliseconds = 1d / 1_000_000d;
                return new[]
                {
                    new CounterReader("draw_calls", ProfilerCategory.Render, "Draw Calls Count", "count", true, 1d),
                    new CounterReader("batches", ProfilerCategory.Render, "Batches Count", "count", true, 1d),
                    new CounterReader("setpass_calls", ProfilerCategory.Render, "SetPass Calls Count", "count", true, 1d),
                    new CounterReader("triangles", ProfilerCategory.Render, "Triangles Count", "count", true, 1d),
                    new CounterReader("vertices", ProfilerCategory.Render, "Vertices Count", "count", true, 1d),
                    new CounterReader("main_thread_ms", ProfilerCategory.Internal, "CPU Main Thread Frame Time", "ms",
                        true, NanosecondsToMilliseconds),
                    new CounterReader("gpu_ms", ProfilerCategory.Internal, "GPU Frame Time", "ms", true,
                        NanosecondsToMilliseconds),
                    new CounterReader("total_used_memory", ProfilerCategory.Memory, "Total Used Memory", "bytes", false, 1d),
                    new CounterReader("total_reserved_memory", ProfilerCategory.Memory, "Total Reserved Memory", "bytes",
                        false, 1d),
                    new CounterReader("gc_used_memory", ProfilerCategory.Memory, "GC Used Memory", "bytes", false, 1d),
                    new CounterReader("gfx_used_memory", ProfilerCategory.Memory, "Gfx Used Memory", "bytes", false, 1d),
                    new CounterReader("system_used_memory", ProfilerCategory.Memory, "System Used Memory", "bytes", false,
                        1d)
                };
            }

            public bool TryRead(out double value)
            {
                value = 0d;
                if (!_recorder.Valid)
                {
                    return false;
                }

                if (_perFrame)
                {
                    if (_recorder.Count == 0) return false;
                    value = _recorder.LastValue * _scale;
                    return true;
                }

                long current = _recorder.CurrentValue;
                if (current <= 0) return false;
                value = current * _scale;
                return true;
            }

            public void Dispose()
            {
                _recorder.Dispose();
            }
        }
    }
}
