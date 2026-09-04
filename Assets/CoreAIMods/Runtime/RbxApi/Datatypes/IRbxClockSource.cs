using System;
using System.Diagnostics;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Every clock the Rbx Lua surface can read. A host swaps this to redefine time: a game
    /// with accelerated days, a deterministic replay, or a server-synchronised session
    /// supplies its own source, and it is the ONLY way to test monotonicity without touching
    /// the machine's real clock. The Lua bindings never call the OS wall clock, Stopwatch, or
    /// the engine frame clock directly; they read everything through this port.
    /// </summary>
    public interface IRbxClockSource
    {
        /// <summary>Scaled game-simulation seconds since world start, behind <c>time()</c>.</summary>
        double GameTimeSeconds { get; }

        /// <summary>Unix epoch seconds (UTC), INTEGER, behind <c>os.time()</c>.</summary>
        long UnixTimeSeconds { get; }

        /// <summary>Fractional process time in seconds, behind <c>os.clock()</c>.</summary>
        double ProcessTimeSeconds { get; }

        /// <summary>Unix epoch seconds (UTC) WITH fraction, behind <c>tick()</c>.</summary>
        double UnixTimeSecondsFractional { get; }
    }

    /// <summary>
    /// Production <see cref="IRbxClockSource"/> over the system wall clock and a Stopwatch
    /// process clock. The scaled game time is NOT owned here: <paramref name="gameTimeSecondsReader"/>
    /// delegates to whatever already owns the scaled clock (the scheduler's time source), so no
    /// second scaled clock exists next to it. Both providers are injectable and every getter is
    /// allocation-free.
    /// </summary>
    public sealed class RbxSystemClockSource : IRbxClockSource
    {
        private readonly Func<double> _gameTimeSecondsReader;
        private readonly Func<DateTimeOffset> _utcNowProvider;
        private readonly long _processStartTimestamp = Stopwatch.GetTimestamp();

        /// <summary>
        /// Creates the production clock. Null readers mean zero game time / system UTC,
        /// matching headless composition before the scheduler is wired.
        /// </summary>
        public RbxSystemClockSource(Func<double> gameTimeSecondsReader = null,
            Func<DateTimeOffset> utcNowProvider = null)
        {
            _gameTimeSecondsReader = gameTimeSecondsReader;
            _utcNowProvider = utcNowProvider ?? DefaultUtcNow;
        }

        /// <inheritdoc />
        public double GameTimeSeconds => _gameTimeSecondsReader?.Invoke() ?? 0d;

        /// <inheritdoc />
        public long UnixTimeSeconds => _utcNowProvider().ToUnixTimeSeconds();

        /// <inheritdoc />
        public double ProcessTimeSeconds
        {
            get
            {
                return (Stopwatch.GetTimestamp() - _processStartTimestamp)
                    / (double)Stopwatch.Frequency;
            }
        }

        /// <inheritdoc />
        public double UnixTimeSecondsFractional
        {
            get
            {
                DateTimeOffset utcNow = _utcNowProvider();
                return utcNow.ToUnixTimeSeconds() + utcNow.Millisecond / 1000d
                    + (utcNow.Ticks % TimeSpan.TicksPerMillisecond) / (double)TimeSpan.TicksPerSecond;
            }
        }

        private static DateTimeOffset DefaultUtcNow()
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
