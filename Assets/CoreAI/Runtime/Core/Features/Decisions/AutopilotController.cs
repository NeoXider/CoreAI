using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Decisions
{
    /// <summary>
    /// Optional answer of an <see cref="IAutopilotActuator"/> to "can you drive right now": a character that has not
    /// spawned, or has died, cannot be handed to the autopilot.
    /// </summary>
    public interface IAutopilotAvailability
    {
        /// <summary>False with an English reason when the autopilot cannot drive now.</summary>
        bool CanDrive(out string reason);
    }

    /// <summary>The result of <see cref="IAutopilotControl.Engage"/>.</summary>
    public readonly struct AutopilotEngageResult
    {
        private readonly string _message;

        /// <summary>Creates a result.</summary>
        public AutopilotEngageResult(bool engaged, bool wasEngaged, string message)
        {
            Engaged = engaged;
            WasEngaged = wasEngaged;
            _message = message ?? "";
        }

        /// <summary>True when the autopilot drives now.</summary>
        public bool Engaged { get; }

        /// <summary>True when it was already driving before the call.</summary>
        public bool WasEngaged { get; }

        /// <summary>Why it did not start; empty when it did.</summary>
        public string Message => _message ?? "";
    }

    /// <summary>A snapshot of an autopilot for the chat, a HUD or the Hub.</summary>
    public sealed class AutopilotStatus
    {
        /// <summary>True while the autopilot drives the character.</summary>
        public bool Engaged { get; set; }

        /// <summary>The consumer id it routes as (normally <see cref="DecisionConsumers.Autopilot"/>).</summary>
        public string ConsumerId { get; set; } = "";

        /// <summary>The goals it chooses among, in order.</summary>
        public IReadOnlyList<string> GoalIds { get; set; } = Array.Empty<string>();

        /// <summary>The last decision of the current engagement; null before the first.</summary>
        public AutopilotDecision LastDecision { get; set; }

        /// <summary>The confidence an answer needs to stand.</summary>
        public double ConfidenceThreshold { get; set; }

        /// <summary>Where decisions are routed now; null when the controller has no router to ask.</summary>
        public DecisionRouteResolution Route { get; set; }

        /// <summary>Why the autopilot last handed control back (for example <c>player_input</c>); empty before.</summary>
        public string LastReleaseReason { get; set; } = "";

        /// <summary>Why it cannot start now; empty when it can.</summary>
        public string UnavailableReason { get; set; } = "";

        /// <summary>Model calls started since creation.</summary>
        public int AskCount { get; set; }
    }

    /// <summary>What the <c>/autopilot</c> command and a host's UI ask of an autopilot.</summary>
    public interface IAutopilotControl
    {
        /// <summary>Raised with the new state and the reason whenever the autopilot starts or stops driving.</summary>
        event Action<bool, string> EngagedChanged;

        /// <summary>True while the autopilot drives the character.</summary>
        bool Engaged { get; }

        /// <summary>Starts driving; refused (with the reason) when the character cannot be driven or the goals are invalid.</summary>
        AutopilotEngageResult Engage();

        /// <summary>Hands control back before returning; false when it was not driving.</summary>
        bool Disengage(string reason = PlayerAutopilotPolicy.ReleasedByHost);

        /// <summary>The current state, the last decision and where decisions come from.</summary>
        AutopilotStatus GetStatus();
    }

    /// <summary>
    /// The host side of <see cref="PlayerAutopilotPolicy"/>: one engagement at a time, a fresh policy per engagement
    /// (so a goal list changed while it was off applies), the readiness check of the actuator, and a status that says
    /// what decides: the routed decision model, or the deterministic fallback and why. Engine-free; drive
    /// <see cref="Tick"/> from one thread as often as you like.
    /// </summary>
    public sealed class AutopilotController : IAutopilotControl, IDisposable
    {
        private readonly PlayerAutopilotOptions _options;
        private readonly IDecisionClient _client;
        private readonly IAutopilotStateProvider _state;
        private readonly IAutopilotActuator _actuator;
        private readonly IDecisionClock _clock;
        private readonly IDecisionTraceSink _trace;
        private readonly IAutopilotFallbackPolicy _fallback;
        private readonly IDecisionRouter _router;
        private readonly Func<DecisionActionSet> _goals;
        private PlayerAutopilotPolicy _policy;
        private DecisionActionSet _engagedGoals;
        private int _previousAsks;
        private string _lastReleaseReason = "";
        private bool _disposed;

        /// <summary>Creates a disengaged controller.</summary>
        /// <param name="options">The policy template; its goals are used when <paramref name="goals"/> is null or answers null.</param>
        /// <param name="client">Normally <c>provider.ForConsumer(DecisionConsumers.Autopilot)</c>.</param>
        /// <param name="state">Describes what the character sees.</param>
        /// <param name="actuator">Drives the character; may also implement <see cref="IAutopilotAvailability"/>.</param>
        /// <param name="clock">Time of the cadence and the deadlines.</param>
        /// <param name="trace">Receives every decision; null discards them.</param>
        /// <param name="fallback">Decides when the model cannot; null keeps the current goal.</param>
        /// <param name="router">Asked only for the status text (which model, or why none); null leaves it out.</param>
        /// <param name="goals">The goal list read at every engagement (for example from the game's own data).</param>
        public AutopilotController(
            PlayerAutopilotOptions options,
            IDecisionClient client,
            IAutopilotStateProvider state,
            IAutopilotActuator actuator,
            IDecisionClock clock,
            IDecisionTraceSink trace = null,
            IAutopilotFallbackPolicy fallback = null,
            IDecisionRouter router = null,
            Func<DecisionActionSet> goals = null)
        {
            _options = options ?? new PlayerAutopilotOptions();
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _trace = trace;
            _fallback = fallback;
            _router = router;
            _goals = goals;
        }

        /// <inheritdoc />
        public event Action<bool, string> EngagedChanged;

        /// <summary>Raised after every goal decision is applied.</summary>
        public event Action<AutopilotDecision> Decided;

        /// <inheritdoc />
        public bool Engaged => _policy != null && _policy.Engaged;

        /// <summary>The consumer id decisions are routed as.</summary>
        public string ConsumerId => string.IsNullOrWhiteSpace(_options.ConsumerId)
            ? DecisionConsumers.Autopilot
            : _options.ConsumerId.Trim();

        /// <summary>The policy of the current or last engagement; null before the first.</summary>
        public PlayerAutopilotPolicy Policy => _policy;

        /// <inheritdoc />
        public AutopilotEngageResult Engage()
        {
            if (_disposed)
            {
                return new AutopilotEngageResult(false, false, "The autopilot was shut down.");
            }

            if (Engaged)
            {
                return new AutopilotEngageResult(true, true, "");
            }

            if (!CanDrive(out string unavailable))
            {
                return new AutopilotEngageResult(false, false, unavailable);
            }

            DecisionActionSet goals = ResolveGoals();
            PlayerAutopilotOptions options = CopyOptions(goals);
            IReadOnlyList<string> errors = options.Validate();
            if (errors.Count > 0)
            {
                return new AutopilotEngageResult(false, false, "The goal list is invalid: " + string.Join(" ", errors));
            }

            DisposePolicy();
            PlayerAutopilotPolicy policy = new(options, _client, _state, _fallback, _actuator, _clock, _trace);
            policy.Released += HandleReleased;
            policy.Decided += HandleDecided;
            _policy = policy;
            _engagedGoals = goals;
            _lastReleaseReason = "";
            policy.Engage();
            Raise(true, "engaged");
            return new AutopilotEngageResult(true, false, "");
        }

        /// <inheritdoc />
        public bool Disengage(string reason = PlayerAutopilotPolicy.ReleasedByHost)
        {
            if (!Engaged)
            {
                return false;
            }

            _policy.Disengage(string.IsNullOrWhiteSpace(reason) ? PlayerAutopilotPolicy.ReleasedByHost : reason);
            return true;
        }

        /// <summary>Real input from the player: control goes back before this returns. No-op while disengaged.</summary>
        public void NotifyPlayerInput()
        {
            if (Engaged)
            {
                _policy.NotifyPlayerInput();
            }
        }

        /// <summary>Reports an event for the next ask while engaged.</summary>
        public void Notify(string eventLine)
        {
            if (Engaged)
            {
                _policy.Notify(eventLine);
            }
        }

        /// <summary>Cheap enough for every frame; asks the model only when the policy's cadence says so.</summary>
        public Task Tick()
        {
            return Engaged ? _policy.Tick() : Task.CompletedTask;
        }

        /// <inheritdoc />
        public AutopilotStatus GetStatus()
        {
            CanDrive(out string unavailable);
            DecisionActionSet goals = Engaged && _engagedGoals != null ? _engagedGoals : ResolveGoals();
            List<string> ids = new();
            foreach (DecisionActionDefinition goal in goals?.Actions ?? Array.Empty<DecisionActionDefinition>())
            {
                if (goal != null)
                {
                    ids.Add(goal.Id);
                }
            }

            DecisionRouteResolution route = null;
            if (_router != null)
            {
                try
                {
                    route = _router.Resolve(ConsumerId);
                }
                catch (Exception)
                {
                    route = null;
                }
            }

            return new AutopilotStatus
            {
                Engaged = Engaged,
                ConsumerId = ConsumerId,
                GoalIds = ids,
                LastDecision = Engaged ? _policy.Current : null,
                ConfidenceThreshold = _options.ConfidenceThreshold,
                Route = route,
                LastReleaseReason = _lastReleaseReason,
                UnavailableReason = unavailable,
                AskCount = _previousAsks + (_policy?.AskCount ?? 0)
            };
        }

        /// <summary>Hands control back and stops for good.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            DisposePolicy();
            _disposed = true;
        }

        private bool CanDrive(out string reason)
        {
            reason = "";
            if (!(_actuator is IAutopilotAvailability availability))
            {
                return true;
            }

            try
            {
                if (availability.CanDrive(out string answer))
                {
                    return true;
                }

                reason = string.IsNullOrWhiteSpace(answer) ? "The character cannot be driven right now." : answer.Trim();
                return false;
            }
            catch (Exception ex)
            {
                reason = "The character could not be checked (" + ex.GetType().Name + ").";
                return false;
            }
        }

        private DecisionActionSet ResolveGoals()
        {
            DecisionActionSet goals = null;
            try
            {
                goals = _goals?.Invoke();
            }
            catch (Exception)
            {
                goals = null;
            }

            return goals ?? _options.Goals ?? DecisionActionSet.Of("idle");
        }

        private PlayerAutopilotOptions CopyOptions(DecisionActionSet goals)
        {
            return new PlayerAutopilotOptions
            {
                PlayerId = _options.PlayerId,
                ConsumerId = ConsumerId,
                Goals = goals,
                QuestionId = _options.QuestionId,
                Instructions = _options.Instructions,
                ConfidenceThreshold = _options.ConfidenceThreshold,
                MinSecondsBetweenAsks = _options.MinSecondsBetweenAsks,
                IdleTickSeconds = _options.IdleTickSeconds,
                MaxRememberedEvents = _options.MaxRememberedEvents
            };
        }

        private void DisposePolicy()
        {
            PlayerAutopilotPolicy policy = _policy;
            if (policy == null)
            {
                return;
            }

            policy.Dispose();
            policy.Released -= HandleReleased;
            policy.Decided -= HandleDecided;
            _previousAsks += policy.AskCount;
            _policy = null;
        }

        private void HandleReleased(string reason)
        {
            _lastReleaseReason = reason ?? "";
            Raise(false, _lastReleaseReason);
        }

        private void HandleDecided(AutopilotDecision decision)
        {
            Decided?.Invoke(decision);
        }

        private void Raise(bool engaged, string reason)
        {
            EngagedChanged?.Invoke(engaged, reason ?? "");
        }
    }

    /// <summary>English sentences about an autopilot, for the chat and logs.</summary>
    public static class AutopilotStatusText
    {
        /// <summary>Who decides: the routed decision model, or the deterministic fallback and why.</summary>
        public static string DescribeDriver(AutopilotStatus status)
        {
            DecisionRouteResolution route = status?.Route;
            if (route == null)
            {
                return "the decision client of '" + (status?.ConsumerId ?? DecisionConsumers.Autopilot) +
                       "', with the built-in fallback when it cannot answer";
            }

            if (route.Ok && route.Endpoint != null)
            {
                string model = (route.Endpoint.Model ?? "").Trim();
                string endpoint = (route.Endpoint.EndpointId ?? "").Trim();
                return model.Length > 0
                    ? "the decision model " + model + " on endpoint '" + endpoint + "'"
                    : "the decision endpoint '" + endpoint + "'";
            }

            string because = route.Message.Length > 0 ? route.Message.TrimEnd('.') : "no decision model is available";
            return "the built-in fallback, because no decision model is available (" + route.Reason + ": " +
                   because + ")";
        }

        /// <summary>Who chose <paramref name="decision"/> and why, for example "the fallback: the decision call failed".</summary>
        public static string DescribeDecision(AutopilotDecision decision, double threshold)
        {
            if (decision == null)
            {
                return "no goal chosen yet";
            }

            if (decision.Source == DecisionSource.Model)
            {
                return "chosen by the model, confidence " + Number(decision.Confidence);
            }

            return "chosen by the fallback: " + DescribeFallback(decision, threshold);
        }

        /// <summary>The fallback reason in words.</summary>
        public static string DescribeFallback(AutopilotDecision decision, double threshold)
        {
            switch (decision?.FallbackReason ?? "")
            {
                case DecisionFallbackReasons.LowConfidence:
                    return "the model was only " + Number(decision.Confidence) + " sure (it needs " + Number(threshold) +
                           ")";
                case DecisionFallbackReasons.CallFailed:
                    string reason = decision.ErrorReason.Length > 0 ? "/" + decision.ErrorReason : "";
                    return "the decision call failed (" + decision.ErrorCode + reason + ")";
                case DecisionFallbackReasons.Refused:
                    return "the model declined the question";
                case DecisionFallbackReasons.NoAnswer:
                    return "the answer had no goal in it";
                case DecisionFallbackReasons.UnknownAction:
                    return "the model chose a goal that is not on the list";
                default:
                    return decision?.FallbackReason ?? "";
            }
        }

        /// <summary>Why control went back, in words.</summary>
        public static string DescribeRelease(string reason)
        {
            switch (reason ?? "")
            {
                case "":
                    return "";
                case PlayerAutopilotPolicy.ReleasedByPlayerInput:
                    return "you took control";
                case PlayerAutopilotPolicy.ReleasedByHost:
                    return "turned off";
                default:
                    return reason;
            }
        }

        private static string Number(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
