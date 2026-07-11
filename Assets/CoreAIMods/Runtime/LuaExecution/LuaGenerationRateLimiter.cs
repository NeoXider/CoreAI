using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Sliding-window rate limiter for LLM-driven Lua script generation/execution. Guards the
    /// envelope pipeline against runaway loops where failing scripts keep scheduling new
    /// Programmer generations (or a misbehaving agent keeps emitting Lua envelopes).
    /// Pure C# (no MoonSharp/UnityEngine) so it compiles under <c>COREAI_NO_LUA</c> and is
    /// EditMode-testable with an injected clock.
    /// </summary>
    public sealed class LuaGenerationRateLimiter
    {
        /// <summary>Default max Lua executions/generation schedules per window.</summary>
        public const int DefaultMaxPerWindow = 20;

        /// <summary>Default sliding-window length in seconds.</summary>
        public const double DefaultWindowSeconds = 60d;

        private readonly object _gate = new();
        private readonly Queue<double> _accepted = new();
        private readonly int _maxPerWindow;
        private readonly double _windowSeconds;
        private long _totalRejected;

        /// <param name="maxPerWindow">Max accepted acquisitions per window; values &lt;= 0 disable the limit.</param>
        /// <param name="windowSeconds">Sliding-window length; values &lt;= 0 fall back to the default.</param>
        public LuaGenerationRateLimiter(
            int maxPerWindow = DefaultMaxPerWindow,
            double windowSeconds = DefaultWindowSeconds)
        {
            _maxPerWindow = maxPerWindow;
            _windowSeconds = windowSeconds > 0d ? windowSeconds : DefaultWindowSeconds;
        }

        /// <summary>Max accepted acquisitions per window (&lt;= 0 means unlimited).</summary>
        public int MaxPerWindow => _maxPerWindow;

        /// <summary>Sliding-window length in seconds.</summary>
        public double WindowSeconds => _windowSeconds;

        /// <summary>Total acquisitions rejected since construction.</summary>
        public long TotalRejected
        {
            get
            {
                lock (_gate)
                {
                    return _totalRejected;
                }
            }
        }

        /// <summary>Accepted acquisitions still inside the window as of <paramref name="nowSeconds"/>.</summary>
        public int GetAcceptedInWindow(double nowSeconds)
        {
            lock (_gate)
            {
                Evict(nowSeconds);
                return _accepted.Count;
            }
        }

        /// <summary>
        /// Tries to consume one generation slot at <paramref name="nowSeconds"/> (monotonic seconds,
        /// e.g. <c>Stopwatch.Elapsed.TotalSeconds</c>). Returns <c>false</c> when the window is full.
        /// </summary>
        public bool TryAcquire(double nowSeconds)
        {
            if (_maxPerWindow <= 0)
            {
                return true;
            }

            lock (_gate)
            {
                Evict(nowSeconds);
                if (_accepted.Count >= _maxPerWindow)
                {
                    _totalRejected++;
                    return false;
                }

                _accepted.Enqueue(nowSeconds);
                return true;
            }
        }

        private void Evict(double nowSeconds)
        {
            while (_accepted.Count > 0 && nowSeconds - _accepted.Peek() >= _windowSeconds)
            {
                _accepted.Dequeue();
            }
        }
    }
}
