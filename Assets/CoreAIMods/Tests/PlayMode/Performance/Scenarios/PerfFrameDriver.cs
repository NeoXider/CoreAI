using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// Drives a reference scene's scheduler once per rendered frame, the way the production
    /// <c>LuaModRuntimeTickDriver</c> does (one <c>ModScheduler.Advance(Time.deltaTime)</c> per Update), and
    /// times that call. The runtime's legacy timer tick is not driven (see <see cref="PerfModWorld"/>).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class PerfFrameDriver : MonoBehaviour
    {
        private readonly List<double> _schedulerMilliseconds = new(1024);
        private PerfModWorld _world;
        private bool _recording;
        private Exception _firstFault;
        private int _faults;

        /// <summary>The scheduler time of each recorded frame, milliseconds.</summary>
        public IReadOnlyList<double> SchedulerMilliseconds => _schedulerMilliseconds;

        /// <summary>The first exception a frame raised, if any (the frame loop keeps running).</summary>
        public Exception FirstFault => _firstFault;

        /// <summary>Frames whose Advance threw.</summary>
        public int Faults => _faults;

        /// <summary>Attaches the world this driver advances.</summary>
        public void Attach(PerfModWorld world)
        {
            _world = world;
        }

        /// <summary>Starts (true) or stops (false) recording scheduler times; frames are advanced either way.</summary>
        public void Record(bool recording)
        {
            if (recording)
            {
                _schedulerMilliseconds.Clear();
            }

            _recording = recording;
        }

        private void Update()
        {
            if (_world == null)
            {
                return;
            }

            long start = Stopwatch.GetTimestamp();
            try
            {
                _world.AdvanceFrame(Time.deltaTime);
            }
            catch (Exception exception)
            {
                _faults++;
                _firstFault ??= exception;
            }

            if (_recording)
            {
                _schedulerMilliseconds.Add((Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency);
            }
        }
    }
}
