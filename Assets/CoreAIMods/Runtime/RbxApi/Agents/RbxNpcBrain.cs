using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai.Npc;
using CoreAI.Decisions;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Rbx.Agents
{
    /// <summary>Configuration of one villager's decision brain. Plain data.</summary>
    public sealed class RbxNpcBrainOptions
    {
        /// <summary>The closed action list the model chooses from.</summary>
        public DecisionActionSet Actions { get; set; } = DefaultActions();

        /// <summary>The action the deterministic fallback takes when no event has a rule.</summary>
        public string DefaultActionId { get; set; } = "wander";

        /// <summary>The fallback's rules: the latest event kind mapped to an action.</summary>
        public IDictionary<string, string> FallbackRules { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NpcDecisionEventKinds.PlayerApproached] = "greet",
            [NpcDialogueEventKinds.SpokenTo] = "face_player",
            [NpcDecisionEventKinds.Attacked] = "flee"
        };

        /// <summary>Re-ask interval when nothing happens, in seconds (a villager need not rethink every two seconds).</summary>
        public double IdleTickSeconds { get; set; } = 6d;

        /// <summary>Hard minimum between two asks, in seconds.</summary>
        public double MinSecondsBetweenAsks { get; set; } = 2d;

        /// <summary>Answers below this confidence go to the fallback.</summary>
        public double ConfidenceThreshold { get; set; } = 0.7d;

        /// <summary>
        /// The local player coming within this many studs reports <c>player_approached</c> and starts a visit; going
        /// beyond one and a half times it ends the visit. 0 reports nothing and tracks no visits.
        /// </summary>
        public double ApproachRadius { get; set; } = 10d;

        /// <summary>
        /// The dialogue action that greets. A greeting is said at most once per visit and only during one, and never
        /// within <see cref="GreetQuietSeconds"/> of the last one; a greeting held back still turns the villager to
        /// the player. An empty id turns the rule off.
        /// </summary>
        public string GreetActionId { get; set; } = "greet";

        /// <summary>The least time between two greetings of one villager, in seconds; 0 keeps only the visit rule.</summary>
        public double GreetQuietSeconds { get; set; } = 120d;

        /// <summary>The movement actions; null uses <see cref="RbxCharacterActionMap.NpcDefaults"/>.</summary>
        public RbxCharacterActionMap Movement { get; set; }

        /// <summary>The dialogue actions; null uses <c>talk</c> and <c>greet</c>.</summary>
        public NpcDialogueActuatorOptions Dialogue { get; set; }

        /// <summary>The speaker id the NPC remembers the local player under; match the chat hearing's player id.</summary>
        public string PlayerSpeakerId { get; set; } = "player";

        /// <summary>What the state text includes.</summary>
        public RbxCharacterStateOptions State { get; set; } = new() { FactsAttribute = RbxAgentAttributes.NpcState };

        /// <summary>Seed of the wander points.</summary>
        public double Seed { get; set; } = 1d;

        /// <summary>A villager's default actions.</summary>
        public static DecisionActionSet DefaultActions()
        {
            return new DecisionActionSet(new[]
            {
                new DecisionActionDefinition("idle", "Stand where you are."),
                new DecisionActionDefinition("wander", "Stroll around near your home."),
                new DecisionActionDefinition("greet", "Greet the player who is close."),
                new DecisionActionDefinition("face_player", "Turn to the player."),
                new DecisionActionDefinition("follow_player", "Walk up to the player."),
                new DecisionActionDefinition("go_home", "Walk back to your home.")
            });
        }
    }

    /// <summary>
    /// One villager's decision brain in an Rbx world, built from the blocks: an <see cref="NpcDecisionPolicy"/> over
    /// the decision model, the NPC's Humanoid (<see cref="RbxHumanoidNpcActuator"/>) and its voice
    /// (<see cref="NpcDialogueActuator"/>), the state of what it sees (<see cref="RbxCharacterStateProvider"/>), the
    /// director's <see cref="INpcDirector.NpcAddressed"/> events (<see cref="NpcAddressedDecisionFeed"/>) and a
    /// <c>player_approached</c> event when the local player comes close. A greeting is said once per visit of the
    /// player and never twice within <see cref="RbxNpcBrainOptions.GreetQuietSeconds"/>, however often the model
    /// chooses it. Deterministic without a decision endpoint: the rule table decides and the decision says so. Call
    /// <see cref="Tick"/> from the game thread.
    /// </summary>
    public sealed class RbxNpcBrain : IDisposable
    {
        private readonly RbxNpcBrainOptions _options;
        private readonly IDecisionClock _clock;
        private readonly NpcAddressedDecisionFeed _feed;
        private bool _playerNear;
        private bool _greetingSpent;
        private double _lastGreetingAt = double.NegativeInfinity;
        private bool _disposed;

        /// <summary>Creates the brain of the director's NPC <paramref name="npcId"/>, whose body is <paramref name="body"/>.</summary>
        public RbxNpcBrain(string npcId, RbxCharacterSubject body, INpcDirector director, IDecisionClient client,
            IDecisionClock clock, IDecisionTraceSink trace = null, RbxNpcBrainOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(npcId))
            {
                throw new ArgumentException("A brain needs the NPC's id.", nameof(npcId));
            }

            if (director == null)
            {
                throw new ArgumentNullException(nameof(director));
            }

            NpcId = npcId.Trim();
            Body = body ?? throw new ArgumentNullException(nameof(body));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options ?? new RbxNpcBrainOptions();
            Movement = new RbxHumanoidNpcActuator(Body, _options.Movement, true, _options.Seed);
            Dialogue = new NpcDialogueActuator(director, _options.Dialogue, Partner);
            State = new RbxCharacterStateProvider(Body, _options.State);
            Policy = new NpcDecisionPolicy(new NpcDecisionPolicyOptions
                {
                    NpcId = NpcId,
                    Actions = _options.Actions,
                    ConfidenceThreshold = _options.ConfidenceThreshold,
                    IdleTickSeconds = _options.IdleTickSeconds,
                    MinSecondsBetweenAsks = _options.MinSecondsBetweenAsks
                },
                client, new GreetingAwareState(this),
                new RuleTableNpcFallbackPolicy(_options.DefaultActionId,
                    _options.FallbackRules == null ? null : new Dictionary<string, string>(_options.FallbackRules)),
                new CompositeNpcActuator(new INpcActuator[] { Movement, new GreetingRule(this) }), clock, trace);
            _feed = new NpcAddressedDecisionFeed(director);
            _feed.Attach(NpcId, Policy);
        }

        /// <summary>The director's id of the NPC.</summary>
        public string NpcId { get; }

        /// <summary>The NPC's body.</summary>
        public RbxCharacterSubject Body { get; }

        /// <summary>The decision policy.</summary>
        public NpcDecisionPolicy Policy { get; }

        /// <summary>The movement actuator.</summary>
        public RbxHumanoidNpcActuator Movement { get; }

        /// <summary>The dialogue actuator.</summary>
        public NpcDialogueActuator Dialogue { get; }

        /// <summary>The state text provider.</summary>
        public RbxCharacterStateProvider State { get; }

        /// <summary>Greeting decisions held back by the visit rule or the quiet time; the villager only turned.</summary>
        public int GreetingsHeld { get; private set; }

        /// <summary>Reports an event (<c>attacked</c>, <c>trade_offered</c>, your own kinds) for the next ask.</summary>
        public void Notify(string kind, string description = "")
        {
            if (!_disposed)
            {
                Policy.Notify(new NpcDecisionEvent(kind, description));
            }
        }

        /// <summary>Call once per frame; asks the model only when the policy's cadence says so.</summary>
        public Task Tick()
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            WatchApproach();
            return Policy.Tick();
        }

        /// <summary>Stops feeding and deciding; the body keeps its last walk.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _feed.Dispose();
            Policy.Dispose();
        }

        private void WatchApproach()
        {
            if (_options.ApproachRadius <= 0d || !Body.TryResolve(out RbxCharacterView self) || !self.Position.HasValue ||
                !Body.TryResolveLocalPlayer(out RbxCharacterView player) || !player.Position.HasValue)
            {
                return;
            }

            double distance = RbxHumanoidDriver.Planar(player.Position.Value - self.Position.Value).Magnitude;
            if (!_playerNear && distance <= _options.ApproachRadius)
            {
                _playerNear = true;
                _greetingSpent = false;
                Policy.Notify(new NpcDecisionEvent(NpcDecisionEventKinds.PlayerApproached,
                    player.Name + " came within " +
                    Math.Round(distance).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " studs"));
            }
            else if (_playerNear && distance > _options.ApproachRadius * 1.5d)
            {
                _playerNear = false;
            }
        }

        /// <summary>True when the greeting decided now may be said; a greeting the visit rule allows spends the visit's one.</summary>
        private bool TakeGreeting()
        {
            if (_options.ApproachRadius > 0d)
            {
                if (!_playerNear || _greetingSpent)
                {
                    return false;
                }

                _greetingSpent = true;
            }

            double quiet = double.IsNaN(_options.GreetQuietSeconds) ? 0d : Math.Max(0d, _options.GreetQuietSeconds);
            return _clock.NowSeconds - _lastGreetingAt >= quiet;
        }

        private void ApplyDialogue(NpcDecision decision)
        {
            string greet = (_options.GreetActionId ?? "").Trim();
            if (decision == null || greet.Length == 0 || !string.Equals(decision.ActionId, greet, StringComparison.Ordinal))
            {
                Dialogue.Apply(decision);
                return;
            }

            if (!TakeGreeting())
            {
                GreetingsHeld++;
                return;
            }

            Dialogue.Apply(decision);
            if (Dialogue.LastOutcome == NpcDialogueOutcome.Said)
            {
                _lastGreetingAt = _clock.NowSeconds;
            }
        }

        private string DescribeForPolicy()
        {
            string state = State.DescribeState();
            string greet = (_options.GreetActionId ?? "").Trim();
            if (greet.Length == 0 || !_playerNear || !_greetingSpent || !_options.Actions.Contains(greet))
            {
                return state;
            }

            // WHY told: a model that keeps choosing the greeting only turns the villager in silence; the line steers
            // its next choice elsewhere.
            return state + "\nYou have greeted the player already; do not " + greet + " again until they leave and come back.";
        }

        private NpcDialoguePartner? Partner()
        {
            if (!Body.TryResolveLocalPlayer(out RbxCharacterView player))
            {
                return null;
            }

            NpcPosition? position = player.Position.HasValue
                ? new NpcPosition(player.Position.Value.X, player.Position.Value.Y, player.Position.Value.Z)
                : (NpcPosition?)null;
            return new NpcDialoguePartner(_options.PlayerSpeakerId, player.Name, position);
        }

        private sealed class GreetingRule : INpcActuator
        {
            private readonly RbxNpcBrain _brain;

            public GreetingRule(RbxNpcBrain brain)
            {
                _brain = brain;
            }

            public void Apply(NpcDecision decision)
            {
                _brain.ApplyDialogue(decision);
            }
        }

        private sealed class GreetingAwareState : INpcStateProvider
        {
            private readonly RbxNpcBrain _brain;

            public GreetingAwareState(RbxNpcBrain brain)
            {
                _brain = brain;
            }

            public string DescribeState()
            {
                return _brain.DescribeForPolicy();
            }
        }
    }
}
