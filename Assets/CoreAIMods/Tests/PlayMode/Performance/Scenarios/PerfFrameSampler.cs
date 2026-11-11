using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// Per-frame engine counters for one measured scene: frame interval, main-thread time, garbage allocated
    /// in the frame, batches, draw calls and SetPass calls, plus memory readings at the end. Every counter
    /// comes from a <see cref="ProfilerRecorder"/>; one the running build does not provide (many are
    /// development-build only) is reported as unavailable, never as zero.
    /// </summary>
    /// <remarks>
    /// WHY a fallback for garbage: "GC Allocated In Frame" is not recorded in a release player. There the
    /// sampler estimates the frame's garbage as the growth of <see cref="GC.GetTotalMemory(bool)"/> over
    /// frames in which no collection ran (Unity's Boehm heap reading includes unreclaimed garbage, so it only
    /// grows between collections); the report names the estimate as such.
    /// WHY <c>LastValue</c> is read one frame late: a recorder holds the value of the last completed frame,
    /// so the sample taken after this frame's Update describes the previous frame. Every series is shifted by
    /// the same frame, and the first sample of a run is the warm-up's last frame.
    /// </remarks>
    public sealed class PerfFrameSampler : IDisposable
    {
        private readonly ProfilerRecorder _mainThread;
        private readonly ProfilerRecorder _gcAllocated;
        private readonly ProfilerRecorder _batches;
        private readonly ProfilerRecorder _drawCalls;
        private readonly ProfilerRecorder _setPassCalls;
        private readonly List<double> _frameMilliseconds = new();
        private readonly List<double> _mainThreadMilliseconds = new();
        private readonly List<double> _gcBytes = new();
        private readonly List<double> _estimatedGcBytes = new();
        private readonly List<double> _batchCounts = new();
        private readonly List<double> _drawCallCounts = new();
        private readonly List<double> _setPassCounts = new();
        private long _lastHeapBytes;
        private int _lastCollectionCount;
        private int _collectionsAtStart;

        /// <summary>Starts the recorders; samples are only kept between <see cref="Begin"/> and <see cref="End"/>.</summary>
        public PerfFrameSampler()
        {
            _mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
            _gcAllocated = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count", 1);
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 1);
            _setPassCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count", 1);
        }

        /// <summary>Clears the series and takes the starting readings.</summary>
        public void Begin()
        {
            _frameMilliseconds.Clear();
            _mainThreadMilliseconds.Clear();
            _gcBytes.Clear();
            _estimatedGcBytes.Clear();
            _batchCounts.Clear();
            _drawCallCounts.Clear();
            _setPassCounts.Clear();
            _lastHeapBytes = GC.GetTotalMemory(false);
            _lastCollectionCount = GC.CollectionCount(0);
            _collectionsAtStart = _lastCollectionCount;
        }

        /// <summary>Records one frame; call once per frame after the frame's Update.</summary>
        public void Sample()
        {
            _frameMilliseconds.Add(Time.unscaledDeltaTime * 1000d);
            if (_mainThread.Valid && _mainThread.Count > 0)
            {
                _mainThreadMilliseconds.Add(_mainThread.LastValue / 1_000_000d);
            }

            if (_gcAllocated.Valid && _gcAllocated.Count > 0)
            {
                _gcBytes.Add(_gcAllocated.LastValue);
            }

            if (_batches.Valid && _batches.Count > 0)
            {
                _batchCounts.Add(_batches.LastValue);
            }

            if (_drawCalls.Valid && _drawCalls.Count > 0)
            {
                _drawCallCounts.Add(_drawCalls.LastValue);
            }

            if (_setPassCalls.Valid && _setPassCalls.Count > 0)
            {
                _setPassCounts.Add(_setPassCalls.LastValue);
            }

            long heap = GC.GetTotalMemory(false);
            int collections = GC.CollectionCount(0);
            if (collections == _lastCollectionCount && heap >= _lastHeapBytes)
            {
                _estimatedGcBytes.Add(heap - _lastHeapBytes);
            }

            _lastHeapBytes = heap;
            _lastCollectionCount = collections;
        }

        /// <summary>Writes the series and the end-of-run memory readings into <paramref name="result"/>.</summary>
        public void End(PerfScenarioResult result)
        {
            result.FrameMilliseconds = PerfDistribution.From(_frameMilliseconds);
            result.MainThreadMilliseconds = PerfDistribution.From(_mainThreadMilliseconds);
            result.GcCollections = GC.CollectionCount(0) - _collectionsAtStart;
            if (_gcBytes.Count > 0)
            {
                result.GcAllocatedBytesPerFrame = PerfDistribution.From(_gcBytes);
                result.GcAllocatedSource = "profiler: GC Allocated In Frame";
            }
            else
            {
                result.GcAllocatedBytesPerFrame = PerfDistribution.From(_estimatedGcBytes);
                result.GcAllocatedSource = "estimate: GC.GetTotalMemory(false) growth over "
                                           + _estimatedGcBytes.Count + " frames without a collection";
                result.Unavailable.Add("GC Allocated In Frame");
            }

            AddSeries(result, "Main Thread", _mainThreadMilliseconds);
            result.Batches = Series(result, "Batches Count", _batchCounts);
            result.DrawCalls = Series(result, "Draw Calls Count", _drawCallCounts);
            result.SetPassCalls = Series(result, "SetPass Calls Count", _setPassCounts);
            result.ManagedHeapBytes = GC.GetTotalMemory(false);
            AddMemory(result, "GC Used Memory");
            AddMemory(result, "GC Reserved Memory");
            AddMemory(result, "Total Used Memory");
            AddMemory(result, "Total Reserved Memory");
            AddMemory(result, "System Used Memory");
            AddMemory(result, "Gfx Used Memory");
            AddMemory(result, "Texture Memory");
            AddMemory(result, "Mesh Memory");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _mainThread.Dispose();
            _gcAllocated.Dispose();
            _batches.Dispose();
            _drawCalls.Dispose();
            _setPassCalls.Dispose();
        }

        private static void AddSeries(PerfScenarioResult result, string counter, List<double> samples)
        {
            if (samples.Count == 0)
            {
                result.Unavailable.Add(counter);
            }
        }

        private static PerfDistribution Series(PerfScenarioResult result, string counter, List<double> samples)
        {
            AddSeries(result, counter, samples);
            return PerfDistribution.From(samples);
        }

        private static void AddMemory(PerfScenarioResult result, string counter)
        {
            using ProfilerRecorder recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, counter);
            if (recorder.Valid && recorder.CurrentValue > 0)
            {
                result.Memory.Add(new KeyValuePair<string, long>(counter, recorder.CurrentValue));
            }
            else
            {
                result.Unavailable.Add(counter);
            }
        }
    }
}
