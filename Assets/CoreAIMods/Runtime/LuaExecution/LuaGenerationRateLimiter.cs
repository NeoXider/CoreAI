using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace CoreAI.Ai
{
    /// <summary>
    /// Sliding-window rate limiter for LLM-driven Lua script generation/execution. Guards the
    /// envelope pipeline against runaway loops where failing scripts keep scheduling new
    /// Programmer generations (or a misbehaving agent keeps emitting Lua envelopes), and the
    /// mutating <c>manage_mods</c> actions when the tool is composed with the same limiter.
    /// Pure C# (no Lua-CSharp/UnityEngine) so it compiles when <c>COREAI_LUA</c> is absent and is
    /// EditMode-testable with an injected clock.
    /// </summary>
    /// <remarks>
    /// Time comes from one of two places, fixed at construction. A limiter built with a clock reads
    /// that clock for every decision and ignores the time a caller passes to
    /// <see cref="TryAcquire(double)"/>, so several tools with their own stopwatches share one
    /// consistent window (the production composition builds it this way). A limiter built without a
    /// clock uses the caller's time, as it always did; its <see cref="TryAcquire(out LuaRateLimitBudget)"/>
    /// and <see cref="GetBudget()"/> read <see cref="SharedClockSeconds"/>, so callers that mix both
    /// forms on a clockless limiter must pass <see cref="SharedClockSeconds"/> as their time.
    /// </remarks>
    public sealed class LuaGenerationRateLimiter
    {
        /// <summary>Default max Lua executions/generation schedules per window.</summary>
        public const int DefaultMaxPerWindow = 20;

        /// <summary>Default sliding-window length in seconds.</summary>
        public const double DefaultWindowSeconds = 60d;

        private static readonly Stopwatch SharedClock = Stopwatch.StartNew();

        private readonly object _gate = new();
        private readonly Queue<double> _accepted = new();
        private readonly Func<double> _clock;
        private int _maxPerWindow;
        private double _windowSeconds;
        private bool _enabled = true;
        private long _totalRejected;

        /// <param name="maxPerWindow">Max accepted acquisitions per window; values &lt;= 0 disable the limit.</param>
        /// <param name="windowSeconds">Sliding-window length; values &lt;= 0 fall back to the default.</param>
        public LuaGenerationRateLimiter(
            int maxPerWindow = DefaultMaxPerWindow,
            double windowSeconds = DefaultWindowSeconds)
            : this(maxPerWindow, windowSeconds, null)
        {
        }

        /// <param name="maxPerWindow">Max accepted acquisitions per window; values &lt;= 0 disable the limit.</param>
        /// <param name="windowSeconds">Sliding-window length; values &lt;= 0 fall back to the default.</param>
        /// <param name="clock">
        /// Monotonic seconds this limiter decides by; null uses the caller's time (see the type remarks).
        /// </param>
        public LuaGenerationRateLimiter(int maxPerWindow, double windowSeconds, Func<double> clock)
        {
            _maxPerWindow = maxPerWindow;
            _windowSeconds = windowSeconds > 0d ? windowSeconds : DefaultWindowSeconds;
            _clock = clock;
        }

        /// <summary>
        /// Monotonic seconds of one process-wide stopwatch: the clock to compose a shared limiter
        /// with, and the time to pass to a clockless one from code that also reads its budget.
        /// </summary>
        public static double SharedClockSeconds => SharedClock.Elapsed.TotalSeconds;

        /// <summary>True when this limiter decides by its own clock and ignores the callers' time.</summary>
        public bool HasClock => _clock != null;

        /// <summary>The time this limiter's parameterless members use: its clock, or <see cref="SharedClockSeconds"/>.</summary>
        public double NowSeconds => _clock != null ? _clock() : SharedClockSeconds;

        /// <summary>Max accepted acquisitions per window (&lt;= 0 means unlimited).</summary>
        public int MaxPerWindow
        {
            get
            {
                lock (_gate)
                {
                    return _maxPerWindow;
                }
            }
        }

        /// <summary>Sliding-window length in seconds.</summary>
        public double WindowSeconds
        {
            get
            {
                lock (_gate)
                {
                    return _windowSeconds;
                }
            }
        }

        /// <summary>False when the limit is switched off: every acquisition is accepted and none is counted.</summary>
        public bool IsEnabled
        {
            get
            {
                lock (_gate)
                {
                    return _enabled;
                }
            }
        }

        /// <summary>True when acquisitions can be refused: enabled with a positive maximum.</summary>
        public bool IsLimiting
        {
            get
            {
                lock (_gate)
                {
                    return _enabled && _maxPerWindow > 0;
                }
            }
        }

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

        /// <summary>
        /// Changes the limit while the game runs (a solo player switches it off, a host tightens it).
        /// Acquisitions already in the window keep counting under the new values.
        /// </summary>
        /// <param name="enabled">False switches the limit off.</param>
        /// <param name="maxPerWindow">Max accepted acquisitions per window; values &lt;= 0 disable the limit.</param>
        /// <param name="windowSeconds">Sliding-window length; values &lt;= 0 keep the current window.</param>
        public void Reconfigure(bool enabled, int maxPerWindow, double windowSeconds)
        {
            lock (_gate)
            {
                _enabled = enabled;
                _maxPerWindow = maxPerWindow;
                if (windowSeconds > 0d)
                {
                    _windowSeconds = windowSeconds;
                }
            }
        }

        /// <summary>Accepted acquisitions still inside the window as of <paramref name="nowSeconds"/>.</summary>
        public int GetAcceptedInWindow(double nowSeconds)
        {
            lock (_gate)
            {
                Evict(Resolve(nowSeconds));
                return _accepted.Count;
            }
        }

        /// <summary>
        /// Tries to consume one generation slot at <paramref name="nowSeconds"/> (monotonic seconds,
        /// e.g. <c>Stopwatch.Elapsed.TotalSeconds</c>). Returns <c>false</c> when the window is full.
        /// A limiter built with a clock reads its clock instead of <paramref name="nowSeconds"/>.
        /// </summary>
        public bool TryAcquire(double nowSeconds)
        {
            return TryAcquireCore(Resolve(nowSeconds), out _);
        }

        /// <summary>
        /// Tries to consume one slot now (<see cref="NowSeconds"/>) and reports the budget left after
        /// the attempt, which holds the reset time a refusal names.
        /// </summary>
        public bool TryAcquire(out LuaRateLimitBudget budget)
        {
            return TryAcquireCore(NowSeconds, out budget);
        }

        /// <summary>The budget as of <see cref="NowSeconds"/>: what a HUD shows.</summary>
        public LuaRateLimitBudget GetBudget()
        {
            return GetBudget(NowSeconds);
        }

        /// <summary>The budget as of <paramref name="nowSeconds"/> (the clock's time when the limiter has one).</summary>
        public LuaRateLimitBudget GetBudget(double nowSeconds)
        {
            lock (_gate)
            {
                double now = Resolve(nowSeconds);
                Evict(now);
                return CreateBudgetLocked(now);
            }
        }

        private bool TryAcquireCore(double now, out LuaRateLimitBudget budget)
        {
            lock (_gate)
            {
                if (!_enabled || _maxPerWindow <= 0)
                {
                    budget = CreateBudgetLocked(now);
                    return true;
                }

                Evict(now);
                if (_accepted.Count >= _maxPerWindow)
                {
                    _totalRejected++;
                    budget = CreateBudgetLocked(now);
                    return false;
                }

                _accepted.Enqueue(now);
                budget = CreateBudgetLocked(now);
                return true;
            }
        }

        private double Resolve(double callerNowSeconds)
        {
            return _clock != null ? _clock() : callerNowSeconds;
        }

        private LuaRateLimitBudget CreateBudgetLocked(double now)
        {
            bool limiting = _enabled && _maxPerWindow > 0;
            int used = limiting ? _accepted.Count : 0;
            double untilNextSlot = 0d;
            if (limiting && used >= _maxPerWindow && _accepted.Count > 0)
            {
                untilNextSlot = Math.Max(0d, _accepted.Peek() + _windowSeconds - now);
            }

            return new LuaRateLimitBudget(limiting, _maxPerWindow, _windowSeconds, used, untilNextSlot);
        }

        private void Evict(double nowSeconds)
        {
            while (_accepted.Count > 0 && nowSeconds - _accepted.Peek() >= _windowSeconds)
            {
                _accepted.Dequeue();
            }
        }
    }

    /// <summary>What a <see cref="LuaGenerationRateLimiter"/> still allows at one moment.</summary>
    public readonly struct LuaRateLimitBudget
    {
        public LuaRateLimitBudget(
            bool isLimiting,
            int maxPerWindow,
            double windowSeconds,
            int used,
            double secondsUntilNextSlot)
        {
            IsLimiting = isLimiting;
            MaxPerWindow = maxPerWindow;
            WindowSeconds = windowSeconds;
            Used = used;
            SecondsUntilNextSlot = secondsUntilNextSlot;
        }

        /// <summary>False when the limit is off or unlimited: nothing is ever refused.</summary>
        public bool IsLimiting { get; }

        public int MaxPerWindow { get; }

        public double WindowSeconds { get; }

        /// <summary>Accepted calls inside the current window.</summary>
        public int Used { get; }

        /// <summary>Calls still accepted in the current window; <see cref="int.MaxValue"/> when not limiting.</summary>
        public int Remaining => IsLimiting ? Math.Max(0, MaxPerWindow - Used) : int.MaxValue;

        /// <summary>Seconds until the next call is accepted again; 0 while calls remain.</summary>
        public double SecondsUntilNextSlot { get; }

        /// <summary>One line a HUD can show, for example "3 of 20 AI changes left (window 60 s)".</summary>
        public string Describe()
        {
            if (!IsLimiting)
            {
                return "AI changes are not rate limited.";
            }

            string window = WindowSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            if (Remaining > 0)
            {
                return Remaining.ToString(CultureInfo.InvariantCulture) + " of "
                       + MaxPerWindow.ToString(CultureInfo.InvariantCulture) + " AI changes left in the "
                       + window + " s window.";
            }

            return "AI change limit reached (" + MaxPerWindow.ToString(CultureInfo.InvariantCulture)
                   + " per " + window + " s); the next one is accepted in "
                   + SecondsUntilNextSlot.ToString("0.0", CultureInfo.InvariantCulture) + " s.";
        }
    }
}
