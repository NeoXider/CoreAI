using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Decisions
{
    /// <summary>
    /// Time for the decision layer: a monotonic clock for budgets and policy ticks, and a delay for per-call
    /// deadlines. Behind an interface so budgets and policies are deterministic under test, and so the Unity host
    /// can supply a player-loop delay (a timer-backed delay never fires in a WebGL player).
    /// </summary>
    public interface IDecisionClock
    {
        /// <summary>Monotonic seconds; only differences are meaningful.</summary>
        double NowSeconds { get; }

        /// <summary>Completes after <paramref name="milliseconds"/>; cancels with <paramref name="cancellationToken"/>.</summary>
        Task DelayAsync(int milliseconds, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Default clock: <see cref="Stopwatch"/> time and the host's <see cref="ILlmAsyncMarshaler.DelayAsync"/>, which
    /// Unity backs with the player loop and portable hosts with the ordinary managed delay.
    /// </summary>
    public sealed class MarshalerDecisionClock : IDecisionClock
    {
        private readonly ILlmAsyncMarshaler _marshaler;
        private readonly Func<double> _now;

        /// <summary>Creates a clock.</summary>
        /// <param name="marshaler">Delay source; null uses the pass-through marshaler.</param>
        /// <param name="now">Time source in seconds; null uses <see cref="Stopwatch"/>.</param>
        public MarshalerDecisionClock(ILlmAsyncMarshaler marshaler = null, Func<double> now = null)
        {
            _marshaler = marshaler ?? PassThroughLlmAsyncMarshaler.Instance;
            _now = now;
        }

        /// <inheritdoc />
        public double NowSeconds => _now != null
            ? _now()
            : (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

        /// <inheritdoc />
        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            return _marshaler.DelayAsync(Math.Max(0, milliseconds), cancellationToken);
        }
    }
}
