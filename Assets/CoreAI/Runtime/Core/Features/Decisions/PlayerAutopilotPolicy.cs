using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Decisions
{
    /// <summary>Describes the player's situation for the model. Implemented by the game.</summary>
    public interface IAutopilotStateProvider
    {
        /// <summary>Plain-text state the goals depend on (resources nearby, threats, the followed character).</summary>
        string DescribeState();
    }

    /// <summary>
    /// Drives the local player through the same controls a human uses (for example <c>Humanoid:MoveTo</c> or a
    /// character motor). Implemented by the game's character package.
    /// </summary>
    public interface IAutopilotActuator
    {
        /// <summary>Starts or switches to pursuing <paramref name="decision"/>'s goal.</summary>
        void Pursue(AutopilotDecision decision);

        /// <summary>Stops driving the character now; the human has control. Must take effect within the frame.</summary>
        void Release();
    }

    /// <summary>What the deterministic autopilot fallback sees.</summary>
    public sealed class AutopilotFallbackContext
    {
        /// <summary>Player id.</summary>
        public string PlayerId { get; set; } = "";

        /// <summary>The goal list.</summary>
        public DecisionActionSet Goals { get; set; }

        /// <summary>Events since the previous ask, oldest first.</summary>
        public IReadOnlyList<string> RecentEvents { get; set; } = Array.Empty<string>();

        /// <summary>Goal currently pursued; empty before the first.</summary>
        public string CurrentGoalId { get; set; } = "";

        /// <summary>Why the model's answer was not used.</summary>
        public DecisionVerdict Verdict { get; set; }
    }

    /// <summary>Chooses a goal when the model cannot.</summary>
    public interface IAutopilotFallbackPolicy
    {
        /// <summary>A goal id from <see cref="AutopilotFallbackContext.Goals"/>.</summary>
        string ChooseGoal(AutopilotFallbackContext context);
    }

    /// <summary>Deterministic default: keep the current goal, else the default goal, else the first goal.</summary>
    public sealed class KeepGoalAutopilotFallbackPolicy : IAutopilotFallbackPolicy
    {
        private readonly string _defaultGoalId;

        /// <summary>Creates the fallback.</summary>
        public KeepGoalAutopilotFallbackPolicy(string defaultGoalId = "")
        {
            _defaultGoalId = defaultGoalId ?? "";
        }

        /// <inheritdoc />
        public string ChooseGoal(AutopilotFallbackContext context)
        {
            DecisionActionSet goals = context?.Goals;
            if (goals == null || goals.Actions.Count == 0)
            {
                return "";
            }

            if (goals.Contains(context.CurrentGoalId))
            {
                return context.CurrentGoalId;
            }

            return goals.Contains(_defaultGoalId) ? _defaultGoalId : goals.Actions[0].Id;
        }
    }

    /// <summary>One goal decision the autopilot acted on.</summary>
    public sealed class AutopilotDecision
    {
        /// <summary>Player id.</summary>
        public string PlayerId { get; set; } = "";

        /// <summary>Chosen goal id; always one of the goal set.</summary>
        public string GoalId { get; set; } = "";

        /// <summary>Who decided.</summary>
        public DecisionSource Source { get; set; }

        /// <summary>For a fallback, the reason from <see cref="DecisionFallbackReasons"/>.</summary>
        public string FallbackReason { get; set; } = "";

        /// <summary>Confidence of the model answer (0 without one).</summary>
        public double Confidence { get; set; }

        /// <summary>Whether the model answer was calibrated.</summary>
        public bool Calibrated { get; set; }

        /// <summary>Error of a failed call.</summary>
        public LlmErrorCode ErrorCode { get; set; }

        /// <summary>Named error reason of a failed call.</summary>
        public string ErrorReason { get; set; } = "";

        /// <summary>Clock time of the decision.</summary>
        public double AtSeconds { get; set; }
    }

    /// <summary>Data of the player autopilot.</summary>
    public sealed class PlayerAutopilotOptions
    {
        /// <summary>Player id recorded on decisions.</summary>
        public string PlayerId { get; set; } = "player";

        /// <summary>Consumer id used for routing and budgets.</summary>
        public string ConsumerId { get; set; } = DecisionConsumers.Autopilot;

        /// <summary>The goals to choose among (for example gather, build, follow, flee).</summary>
        public DecisionActionSet Goals { get; set; } = DecisionActionSet.Of("idle");

        /// <summary>Question id of the goal question.</summary>
        public string QuestionId { get; set; } = "goal";

        /// <summary>The question asked every time.</summary>
        public string Instructions { get; set; } = "Which goal should the player's character pursue now?";

        /// <summary>Answers below this confidence go to the fallback.</summary>
        public double ConfidenceThreshold { get; set; } = 0.7d;

        /// <summary>Hard minimum between two model calls, events included.</summary>
        public double MinSecondsBetweenAsks { get; set; } = 2d;

        /// <summary>Re-ask interval when nothing happens.</summary>
        public double IdleTickSeconds { get; set; } = 2d;

        /// <summary>Events kept for the next ask.</summary>
        public int MaxRememberedEvents { get; set; } = 4;

        /// <summary>Validation errors.</summary>
        public IReadOnlyList<string> Validate()
        {
            List<string> errors = new();
            if (Goals == null)
            {
                errors.Add("Goals are required.");
            }
            else
            {
                errors.AddRange(Goals.Validate());
            }

            if (!DecisionRequestValidator.IsValidQuestionId(QuestionId))
            {
                errors.Add($"Question id '{QuestionId}' is not a valid question id.");
            }

            if (double.IsNaN(ConfidenceThreshold) || ConfidenceThreshold < 0d || ConfidenceThreshold > 1d)
            {
                errors.Add("ConfidenceThreshold must be within 0..1.");
            }

            if (double.IsNaN(MinSecondsBetweenAsks) || MinSecondsBetweenAsks <= 0d)
            {
                errors.Add("MinSecondsBetweenAsks must be positive: the model is never asked per frame.");
            }

            return errors;
        }
    }

    /// <summary>
    /// An opt-in autopilot for the local player. Off until <see cref="Engage"/>; then it chooses among goals on
    /// events and on the slow idle tick (same cadence rules as <see cref="NpcDecisionPolicy"/>), with the same
    /// threshold fallback. Any player input (<see cref="NotifyPlayerInput"/>) hands control back at once: the
    /// actuator is released before the call returns, a call in flight is cancelled, and its late answer is
    /// dropped. Re-engaging is the host's choice. Drive one instance from one thread.
    /// </summary>
    public sealed class PlayerAutopilotPolicy : IDisposable
    {
        /// <summary>Release reason for player input.</summary>
        public const string ReleasedByPlayerInput = "player_input";

        /// <summary>Release reason for a host call to <see cref="Disengage"/> without a reason.</summary>
        public const string ReleasedByHost = "host";

        private readonly PlayerAutopilotOptions _options;
        private readonly IDecisionClient _client;
        private readonly IAutopilotStateProvider _state;
        private readonly IAutopilotFallbackPolicy _fallback;
        private readonly IAutopilotActuator _actuator;
        private readonly IDecisionClock _clock;
        private readonly IDecisionTraceSink _trace;
        private readonly List<string> _pending = new();
        private DecisionCadence _cadence;
        private CancellationTokenSource _engagement;
        private int _engagementId;
        private int _askCount;
        private bool _disposed;

        /// <summary>Creates a disengaged autopilot; throws <see cref="ArgumentException"/> for invalid options.</summary>
        public PlayerAutopilotPolicy(
            PlayerAutopilotOptions options,
            IDecisionClient client,
            IAutopilotStateProvider state,
            IAutopilotFallbackPolicy fallback,
            IAutopilotActuator actuator,
            IDecisionClock clock,
            IDecisionTraceSink trace = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            IReadOnlyList<string> errors = options.Validate();
            if (errors.Count > 0)
            {
                throw new ArgumentException("Invalid autopilot options: " + string.Join(" ", errors), nameof(options));
            }

            _client = client ?? throw new ArgumentNullException(nameof(client));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _fallback = fallback ?? new KeepGoalAutopilotFallbackPolicy();
            _actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _trace = trace ?? new NullDecisionTraceSink();
        }

        /// <summary>Raised after every goal decision is applied.</summary>
        public event Action<AutopilotDecision> Decided;

        /// <summary>Raised when control goes back to the player, with the reason.</summary>
        public event Action<string> Released;

        /// <summary>True while the autopilot drives the character.</summary>
        public bool Engaged { get; private set; }

        /// <summary>The last goal decision applied in the current engagement; null when none.</summary>
        public AutopilotDecision Current { get; private set; }

        /// <summary>Model calls started since creation.</summary>
        public int AskCount => _askCount;

        /// <summary>Starts driving; the first goal is asked on the next <see cref="Tick"/>.</summary>
        public void Engage()
        {
            if (_disposed || Engaged)
            {
                return;
            }

            Engaged = true;
            _engagementId++;
            _engagement = new CancellationTokenSource();
            _cadence = new DecisionCadence(_options.IdleTickSeconds, _options.MinSecondsBetweenAsks);
            _pending.Clear();
            Current = null;
        }

        /// <summary>Any player input: hands control back before returning.</summary>
        public void NotifyPlayerInput()
        {
            Disengage(ReleasedByPlayerInput);
        }

        /// <summary>Hands control back before returning; a call in flight is cancelled and its answer dropped.</summary>
        public void Disengage(string reason = ReleasedByHost)
        {
            if (!Engaged)
            {
                return;
            }

            Engaged = false;
            _engagementId++;
            CancellationTokenSource engagement = _engagement;
            _engagement = null;
            _pending.Clear();
            try
            {
                _actuator.Release();
            }
            finally
            {
                engagement?.Cancel();
            }

            Released?.Invoke(string.IsNullOrEmpty(reason) ? ReleasedByHost : reason);
        }

        /// <summary>Reports an event while engaged; ignored when disengaged.</summary>
        public void Notify(string eventLine)
        {
            if (!Engaged || string.IsNullOrWhiteSpace(eventLine))
            {
                return;
            }

            _pending.Add(eventLine);
            int keep = Math.Max(1, _options.MaxRememberedEvents);
            while (_pending.Count > keep)
            {
                _pending.RemoveAt(0);
            }

            _cadence.NotifyEvent();
        }

        /// <summary>
        /// Cheap enough to call every frame. While engaged, starts a model call when one is due and returns a task
        /// that completes once its decision is applied (or dropped); otherwise returns a completed task.
        /// </summary>
        public Task Tick()
        {
            if (_disposed || !Engaged || !_cadence.TryBegin(_clock.NowSeconds))
            {
                return Task.CompletedTask;
            }

            _askCount++;
            List<string> events = new(_pending);
            _pending.Clear();
            return AskAsync(events, _engagementId, _cadence, _engagement.Token);
        }

        /// <summary>Releases control if engaged and stops the policy.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Disengage(ReleasedByHost);
            _disposed = true;
        }

        private async Task AskAsync(List<string> events, int engagementId, DecisionCadence cadence,
            CancellationToken engagementToken)
        {
            DecisionResult result;
            try
            {
                result = await _client.DecideAsync(BuildRequest(events), engagementToken);
            }
            catch (OperationCanceledException) when (engagementToken.IsCancellationRequested)
            {
                cadence.Complete();
                return;
            }
            catch (Exception ex)
            {
                result = DecisionResult.Failure(LlmErrorCode.ProviderError, DecisionErrorReasons.ClientThrew,
                    "The decision client threw " + ex.GetType().Name + ".");
            }

            cadence.Complete();
            if (_disposed || !Engaged || engagementId != _engagementId)
            {
                return;
            }

            Apply(Decide(result, events));
        }

        private DecisionRequest BuildRequest(List<string> events)
        {
            string state = DecisionChoice.ComposeState(_state.DescribeState(), events);
            DecisionQuestion question = _options.Goals.ToChoiceQuestion(_options.QuestionId, _options.Instructions);
            return new DecisionRequest(state, question) { ConsumerId = _options.ConsumerId };
        }

        private AutopilotDecision Decide(DecisionResult result, List<string> events)
        {
            DecisionVerdict verdict = DecisionChoice.Evaluate(result, _options.QuestionId, _options.Goals,
                _options.ConfidenceThreshold);
            AutopilotDecision decision = new()
            {
                PlayerId = _options.PlayerId,
                Confidence = verdict.Confidence,
                Calibrated = verdict.Calibrated,
                ErrorCode = verdict.ErrorCode,
                ErrorReason = verdict.ErrorReason,
                AtSeconds = _clock.NowSeconds
            };

            if (verdict.Accepted)
            {
                decision.GoalId = verdict.OptionId;
                decision.Source = DecisionSource.Model;
                return decision;
            }

            string chosen = _fallback.ChooseGoal(new AutopilotFallbackContext
            {
                PlayerId = _options.PlayerId,
                Goals = _options.Goals,
                RecentEvents = events,
                CurrentGoalId = Current?.GoalId ?? "",
                Verdict = verdict
            });
            decision.GoalId = _options.Goals.Contains(chosen) ? chosen : _options.Goals.Actions[0].Id;
            decision.Source = DecisionSource.Fallback;
            decision.FallbackReason = verdict.FallbackReason;
            return decision;
        }

        private void Apply(AutopilotDecision decision)
        {
            Current = decision;
            _trace.Record(new DecisionTrace
            {
                ConsumerId = _options.ConsumerId,
                SubjectId = decision.PlayerId,
                OptionId = decision.GoalId,
                Source = decision.Source,
                FallbackReason = decision.FallbackReason,
                Confidence = decision.Confidence,
                Calibrated = decision.Calibrated,
                ErrorCode = decision.ErrorCode,
                ErrorReason = decision.ErrorReason,
                AtSeconds = decision.AtSeconds
            });
            _actuator.Pursue(decision);
            Decided?.Invoke(decision);
        }
    }
}
