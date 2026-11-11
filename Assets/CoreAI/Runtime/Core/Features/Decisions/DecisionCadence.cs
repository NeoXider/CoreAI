using System;

namespace CoreAI.Decisions
{
    /// <summary>
    /// When a consumer may ask a decision model: on a slow idle tick and on events, never two asks closer than
    /// <see cref="MinSecondsBetweenAsks"/>, and never while one is in flight. A host may call
    /// <see cref="TryBegin"/> every frame; it is a few comparisons, and the model is asked at most once per
    /// spacing however often it is called.
    /// </summary>
    public sealed class DecisionCadence
    {
        private double _lastAskAt = double.NegativeInfinity;
        private double _nextTickAt = double.NegativeInfinity;

        /// <summary>Creates a cadence.</summary>
        /// <param name="idleTickSeconds">Re-ask interval without events; raised to the spacing when lower.</param>
        /// <param name="minSecondsBetweenAsks">Hard minimum between two asks, events included.</param>
        public DecisionCadence(double idleTickSeconds, double minSecondsBetweenAsks)
        {
            MinSecondsBetweenAsks = Clean(minSecondsBetweenAsks);
            IdleTickSeconds = Math.Max(Clean(idleTickSeconds), MinSecondsBetweenAsks);
        }

        /// <summary>Hard minimum between two asks.</summary>
        public double MinSecondsBetweenAsks { get; }

        /// <summary>Re-ask interval without events.</summary>
        public double IdleTickSeconds { get; }

        /// <summary>True while an ask is in flight.</summary>
        public bool InFlight { get; private set; }

        /// <summary>True when an event is waiting for the next ask.</summary>
        public bool EventPending { get; private set; }

        /// <summary>Asks started so far.</summary>
        public int AskCount { get; private set; }

        /// <summary>Marks that something happened; the next ask comes as soon as the spacing allows.</summary>
        public void NotifyEvent()
        {
            EventPending = true;
        }

        /// <summary>
        /// True when an ask should start at <paramref name="now"/>; the caller must then call
        /// <see cref="Complete"/> when it finishes.
        /// </summary>
        public bool TryBegin(double now)
        {
            if (InFlight || now < _lastAskAt + MinSecondsBetweenAsks)
            {
                return false;
            }

            if (!EventPending && now < _nextTickAt)
            {
                return false;
            }

            InFlight = true;
            EventPending = false;
            _lastAskAt = now;
            _nextTickAt = now + IdleTickSeconds;
            AskCount++;
            return true;
        }

        /// <summary>Ends the ask started by the last successful <see cref="TryBegin"/>.</summary>
        public void Complete()
        {
            InFlight = false;
        }

        private static double Clean(double seconds)
        {
            return double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0d ? 0d : seconds;
        }
    }
}
