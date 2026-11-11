using System.Collections.Generic;

namespace CoreAI.Decisions
{
    /// <summary>
    /// Per-consumer limits on decision calls. Decisions are only worth having while they are fresh, so a call
    /// over budget is refused at once (<see cref="CoreAI.Ai.LlmErrorCode.ClientLimitExceeded"/>) instead of being
    /// queued, and the consumer's deterministic fallback decides.
    /// </summary>
    public sealed class DecisionBudget
    {
        /// <summary>Default per-call deadline: the sub-second target for a local decision model.</summary>
        public const int DefaultDeadlineMilliseconds = 900;

        /// <summary>
        /// Sustained calls per second (a token bucket holding <c>max(1, rate)</c> calls). 0 or less is unlimited.
        /// </summary>
        public double MaxCallsPerSecond { get; set; } = 4d;

        /// <summary>Calls allowed in flight at once. 0 or less is unlimited.</summary>
        public int MaxInFlight { get; set; } = 2;

        /// <summary>Per-call deadline in milliseconds. 0 or less disables it (the transport ceiling still applies).</summary>
        public int DeadlineMilliseconds { get; set; } = DefaultDeadlineMilliseconds;

        /// <summary>A budget with no limits.</summary>
        public static DecisionBudget Unlimited() => new() { MaxCallsPerSecond = 0d, MaxInFlight = 0, DeadlineMilliseconds = 0 };

        /// <summary>A budget for a remote endpoint: same caps, a 3 s deadline.</summary>
        public static DecisionBudget Remote() => new() { DeadlineMilliseconds = 3000 };

        /// <summary>Copies this budget.</summary>
        public DecisionBudget Clone() => new()
        {
            MaxCallsPerSecond = MaxCallsPerSecond, MaxInFlight = MaxInFlight, DeadlineMilliseconds = DeadlineMilliseconds
        };

        /// <summary>Validation errors; a non-finite rate is the only invalid value.</summary>
        public IReadOnlyList<string> Validate()
        {
            List<string> errors = new();
            if (double.IsNaN(MaxCallsPerSecond) || double.IsInfinity(MaxCallsPerSecond))
            {
                errors.Add("MaxCallsPerSecond must be a finite number.");
            }

            return errors;
        }
    }
}
