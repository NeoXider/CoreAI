using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Decisions
{
    /// <summary>Event kinds the NPC policy understands out of the box; any other string works too.</summary>
    public static class NpcDecisionEventKinds
    {
        /// <summary>The player came close.</summary>
        public const string PlayerApproached = "player_approached";

        /// <summary>The NPC was attacked.</summary>
        public const string Attacked = "attacked";

        /// <summary>Someone offered a trade.</summary>
        public const string TradeOffered = "trade_offered";
    }

    /// <summary>Something that happened to an NPC, reported by the game.</summary>
    public sealed class NpcDecisionEvent
    {
        /// <summary>Creates an event.</summary>
        public NpcDecisionEvent(string kind, string description = "")
        {
            Kind = kind ?? "";
            Description = description ?? "";
        }

        /// <summary>Event kind (see <see cref="NpcDecisionEventKinds"/>).</summary>
        public string Kind { get; }

        /// <summary>Free text for the model, for example "the player hit you with a sword".</summary>
        public string Description { get; }

        /// <summary>The line the state text shows.</summary>
        public override string ToString()
        {
            return Description.Length > 0 ? Kind + ": " + Description : Kind;
        }
    }

    /// <summary>Describes the NPC's situation for the model. Implemented by the game.</summary>
    public interface INpcStateProvider
    {
        /// <summary>Plain-text state: health, nearby characters, inventory, whatever the actions depend on.</summary>
        string DescribeState();
    }

    /// <summary>Carries a decision out (moving a character, starting an animation). Implemented by the game's character package.</summary>
    public interface INpcActuator
    {
        /// <summary>Acts on <paramref name="decision"/>; called on the thread that drives the policy.</summary>
        void Apply(NpcDecision decision);
    }

    /// <summary>What the deterministic fallback sees.</summary>
    public sealed class NpcFallbackContext
    {
        /// <summary>NPC id.</summary>
        public string NpcId { get; set; } = "";

        /// <summary>The closed action list.</summary>
        public DecisionActionSet Actions { get; set; }

        /// <summary>Events since the previous ask, oldest first.</summary>
        public IReadOnlyList<NpcDecisionEvent> RecentEvents { get; set; } = Array.Empty<NpcDecisionEvent>();

        /// <summary>Why the model's answer was not used.</summary>
        public DecisionVerdict Verdict { get; set; }
    }

    /// <summary>Decides when the model cannot: a behaviour tree, a utility scorer or a rule table.</summary>
    public interface INpcFallbackPolicy
    {
        /// <summary>An action id from <see cref="NpcFallbackContext.Actions"/>.</summary>
        string ChooseAction(NpcFallbackContext context);
    }

    /// <summary>
    /// Deterministic default fallback: the action mapped to the most recent event kind that has a rule, otherwise
    /// the default action, otherwise the first action of the set.
    /// </summary>
    public sealed class RuleTableNpcFallbackPolicy : INpcFallbackPolicy
    {
        private readonly string _defaultActionId;
        private readonly Dictionary<string, string> _actionByEventKind;

        /// <summary>Creates a rule table.</summary>
        public RuleTableNpcFallbackPolicy(string defaultActionId = "",
            IReadOnlyDictionary<string, string> actionByEventKind = null)
        {
            _defaultActionId = defaultActionId ?? "";
            _actionByEventKind = new Dictionary<string, string>(StringComparer.Ordinal);
            if (actionByEventKind != null)
            {
                foreach (KeyValuePair<string, string> rule in actionByEventKind)
                {
                    _actionByEventKind[rule.Key ?? ""] = rule.Value ?? "";
                }
            }
        }

        /// <inheritdoc />
        public string ChooseAction(NpcFallbackContext context)
        {
            DecisionActionSet actions = context?.Actions;
            IReadOnlyList<NpcDecisionEvent> events = context?.RecentEvents ?? Array.Empty<NpcDecisionEvent>();
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (_actionByEventKind.TryGetValue(events[i].Kind, out string mapped) && actions != null &&
                    actions.Contains(mapped))
                {
                    return mapped;
                }
            }

            if (actions != null && actions.Contains(_defaultActionId))
            {
                return _defaultActionId;
            }

            return actions != null && actions.Actions.Count > 0 ? actions.Actions[0].Id : "";
        }
    }

    /// <summary>One decision the NPC acted on.</summary>
    public sealed class NpcDecision
    {
        /// <summary>NPC id.</summary>
        public string NpcId { get; set; } = "";

        /// <summary>Chosen action id; always one of the action set.</summary>
        public string ActionId { get; set; } = "";

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

        /// <summary>Event kinds this decision answered, oldest first.</summary>
        public IReadOnlyList<string> TriggerEventKinds { get; set; } = Array.Empty<string>();

        /// <summary>Clock time of the decision.</summary>
        public double AtSeconds { get; set; }
    }

    /// <summary>Data of one NPC brain.</summary>
    public sealed class NpcDecisionPolicyOptions
    {
        /// <summary>NPC id recorded on decisions.</summary>
        public string NpcId { get; set; } = "npc";

        /// <summary>Consumer id used for routing and budgets.</summary>
        public string ConsumerId { get; set; } = DecisionConsumers.NpcBrain;

        /// <summary>The closed action list.</summary>
        public DecisionActionSet Actions { get; set; } = DecisionActionSet.Of("idle");

        /// <summary>Question id of the action question.</summary>
        public string QuestionId { get; set; } = "action";

        /// <summary>The question asked every time.</summary>
        public string Instructions { get; set; } = "Which action should this character take now?";

        /// <summary>Answers below this confidence go to the fallback (0.7 is the published NPC-brain practice).</summary>
        public double ConfidenceThreshold { get; set; } = 0.7d;

        /// <summary>Hard minimum between two model calls for this NPC, events included.</summary>
        public double MinSecondsBetweenAsks { get; set; } = 2d;

        /// <summary>Re-ask interval when nothing happens; never below <see cref="MinSecondsBetweenAsks"/>.</summary>
        public double IdleTickSeconds { get; set; } = 2d;

        /// <summary>Events kept for the next ask; older ones are dropped.</summary>
        public int MaxRememberedEvents { get; set; } = 4;

        /// <summary>Validation errors.</summary>
        public IReadOnlyList<string> Validate()
        {
            List<string> errors = new();
            if (Actions == null)
            {
                errors.Add("Actions are required.");
            }
            else
            {
                errors.AddRange(Actions.Validate());
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
    /// An NPC brain over a decision model. The game reports events (<see cref="Notify"/>) and calls
    /// <see cref="Tick"/> from its loop as often as it likes; the model is asked only on events and on the slow
    /// idle tick, never twice within <see cref="NpcDecisionPolicyOptions.MinSecondsBetweenAsks"/> and never while a
    /// call is in flight. The model chooses from the closed action list; below the confidence threshold, or on any
    /// failure, the deterministic fallback chooses and the decision is marked <see cref="DecisionSource.Fallback"/>
    /// with the reason. Drive one instance from one thread.
    /// </summary>
    public sealed class NpcDecisionPolicy : IDisposable
    {
        private readonly NpcDecisionPolicyOptions _options;
        private readonly IDecisionClient _client;
        private readonly INpcStateProvider _state;
        private readonly INpcFallbackPolicy _fallback;
        private readonly INpcActuator _actuator;
        private readonly IDecisionClock _clock;
        private readonly IDecisionTraceSink _trace;
        private readonly DecisionCadence _cadence;
        private readonly List<NpcDecisionEvent> _pending = new();
        private readonly CancellationTokenSource _lifetime = new();
        private bool _disposed;

        /// <summary>Creates a policy; throws <see cref="ArgumentException"/> for invalid options.</summary>
        public NpcDecisionPolicy(
            NpcDecisionPolicyOptions options,
            IDecisionClient client,
            INpcStateProvider state,
            INpcFallbackPolicy fallback,
            INpcActuator actuator,
            IDecisionClock clock,
            IDecisionTraceSink trace = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            IReadOnlyList<string> errors = options.Validate();
            if (errors.Count > 0)
            {
                throw new ArgumentException("Invalid NPC decision options: " + string.Join(" ", errors), nameof(options));
            }

            _client = client ?? throw new ArgumentNullException(nameof(client));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _fallback = fallback ?? new RuleTableNpcFallbackPolicy();
            _actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _trace = trace ?? new NullDecisionTraceSink();
            _cadence = new DecisionCadence(options.IdleTickSeconds, options.MinSecondsBetweenAsks);
        }

        /// <summary>Raised after every decision is applied.</summary>
        public event Action<NpcDecision> Decided;

        /// <summary>The last decision applied; null before the first.</summary>
        public NpcDecision Current { get; private set; }

        /// <summary>Model calls started so far.</summary>
        public int AskCount => _cadence.AskCount;

        /// <summary>True while a model call is in flight.</summary>
        public bool Asking => _cadence.InFlight;

        /// <summary>Reports an event; the next ask comes as soon as the spacing allows and includes it.</summary>
        public void Notify(NpcDecisionEvent decisionEvent)
        {
            if (decisionEvent == null || _disposed)
            {
                return;
            }

            _pending.Add(decisionEvent);
            int keep = Math.Max(1, _options.MaxRememberedEvents);
            while (_pending.Count > keep)
            {
                _pending.RemoveAt(0);
            }

            _cadence.NotifyEvent();
        }

        /// <summary>
        /// Cheap enough to call every frame. Starts a model call when one is due and returns a task that completes
        /// once its decision is applied; otherwise returns a completed task.
        /// </summary>
        public Task Tick()
        {
            if (_disposed || !_cadence.TryBegin(_clock.NowSeconds))
            {
                return Task.CompletedTask;
            }

            List<NpcDecisionEvent> events = new(_pending);
            _pending.Clear();
            return AskAsync(events);
        }

        /// <summary>Stops the policy and cancels a call in flight; no decision is applied afterwards.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
        }

        private async Task AskAsync(List<NpcDecisionEvent> events)
        {
            DecisionResult result;
            try
            {
                result = await _client.DecideAsync(BuildRequest(events), _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                _cadence.Complete();
                return;
            }
            catch (Exception ex)
            {
                result = DecisionResult.Failure(LlmErrorCode.ProviderError, DecisionErrorReasons.ClientThrew,
                    "The decision client threw " + ex.GetType().Name + ".");
            }

            _cadence.Complete();
            if (_disposed)
            {
                return;
            }

            Apply(Decide(result, events));
        }

        private DecisionRequest BuildRequest(List<NpcDecisionEvent> events)
        {
            List<string> lines = new();
            foreach (NpcDecisionEvent decisionEvent in events)
            {
                lines.Add(decisionEvent.ToString());
            }

            string state = DecisionChoice.ComposeState(_state.DescribeState(), lines);
            DecisionQuestion question = _options.Actions.ToChoiceQuestion(_options.QuestionId, _options.Instructions);
            return new DecisionRequest(state, question) { ConsumerId = _options.ConsumerId };
        }

        private NpcDecision Decide(DecisionResult result, List<NpcDecisionEvent> events)
        {
            DecisionVerdict verdict = DecisionChoice.Evaluate(result, _options.QuestionId, _options.Actions,
                _options.ConfidenceThreshold);
            List<string> kinds = new();
            foreach (NpcDecisionEvent decisionEvent in events)
            {
                kinds.Add(decisionEvent.Kind);
            }

            NpcDecision decision = new()
            {
                NpcId = _options.NpcId,
                Confidence = verdict.Confidence,
                Calibrated = verdict.Calibrated,
                ErrorCode = verdict.ErrorCode,
                ErrorReason = verdict.ErrorReason,
                TriggerEventKinds = kinds,
                AtSeconds = _clock.NowSeconds
            };

            if (verdict.Accepted)
            {
                decision.ActionId = verdict.OptionId;
                decision.Source = DecisionSource.Model;
                return decision;
            }

            string chosen = _fallback.ChooseAction(new NpcFallbackContext
            {
                NpcId = _options.NpcId, Actions = _options.Actions, RecentEvents = events, Verdict = verdict
            });
            decision.ActionId = _options.Actions.Contains(chosen) ? chosen : _options.Actions.Actions[0].Id;
            decision.Source = DecisionSource.Fallback;
            decision.FallbackReason = verdict.FallbackReason;
            return decision;
        }

        private void Apply(NpcDecision decision)
        {
            Current = decision;
            _trace.Record(new DecisionTrace
            {
                ConsumerId = _options.ConsumerId,
                SubjectId = decision.NpcId,
                OptionId = decision.ActionId,
                Source = decision.Source,
                FallbackReason = decision.FallbackReason,
                Confidence = decision.Confidence,
                Calibrated = decision.Calibrated,
                ErrorCode = decision.ErrorCode,
                ErrorReason = decision.ErrorReason,
                AtSeconds = decision.AtSeconds
            });
            _actuator.Apply(decision);
            Decided?.Invoke(decision);
        }
    }
}
