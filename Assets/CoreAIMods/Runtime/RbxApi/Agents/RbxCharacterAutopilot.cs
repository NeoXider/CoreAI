using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Decisions;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Agents
{
    /// <summary>Configuration of the local player's autopilot in an Rbx world. Plain data.</summary>
    public sealed class RbxCharacterAutopilotOptions
    {
        /// <summary>The policy template: cadence, threshold, consumer id; its goals are used when the world declares none.</summary>
        public PlayerAutopilotOptions Policy { get; set; } = new() { Goals = DefaultGoals() };

        /// <summary>The goal the deterministic fallback starts with when there is no current goal; ignored when not on the list.</summary>
        public string DefaultGoalId { get; set; } = "wander";

        /// <summary>The local player's actor id; empty means the one player of a solo world.</summary>
        public string LocalActorId { get; set; } = "";

        /// <summary>The goals C# carries out itself; every other goal is only published for a mod.</summary>
        public RbxCharacterActionMap Actions { get; set; } = RbxCharacterActionMap.AutopilotDefaults();

        /// <summary>Which input takes control back.</summary>
        public RbxPlayerInputOptions Input { get; set; } = new();

        /// <summary>What the state text includes.</summary>
        public RbxCharacterStateOptions State { get; set; } = new()
        {
            Role = "the player's character, driven by the autopilot",
            FactsAttribute = RbxAgentAttributes.AutopilotState
        };

        /// <summary>
        /// True follows the attribute contract on the local Player (<see cref="RbxAgentAttributes"/>): goals and
        /// facts from a mod, requests through <c>Autopilot</c>, the state and the goal published back.
        /// </summary>
        public bool UseWorldAttributes { get; set; } = true;

        /// <summary>Seed of the wander points.</summary>
        public double Seed { get; set; } = 1d;

        /// <summary>The goals of a world that declares none: plain movement.</summary>
        public static DecisionActionSet DefaultGoals()
        {
            return new DecisionActionSet(new[]
            {
                new DecisionActionDefinition("wander", "Walk around near where you started."),
                new DecisionActionDefinition("follow", "Walk to the nearest character and stay close."),
                new DecisionActionDefinition("flee", "Run away from the nearest character."),
                new DecisionActionDefinition("idle", "Stand still.")
            });
        }

        /// <summary>
        /// Reads a goal list a mod wrote (<see cref="RbxAgentAttributes.AutopilotGoals"/>): entries separated by
        /// commas, or by semicolons when the text holds any, each <c>id</c> or <c>id: description</c>. Null for an
        /// empty text.
        /// </summary>
        public static DecisionActionSet ParseGoals(string text)
        {
            string source = (text ?? "").Trim();
            if (source.Length == 0)
            {
                return null;
            }

            char separator = source.IndexOf(';') >= 0 ? ';' : ',';
            List<DecisionActionDefinition> goals = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (string entry in source.Split(separator))
            {
                string trimmed = entry.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                int colon = trimmed.IndexOf(':');
                string id = (colon >= 0 ? trimmed.Substring(0, colon) : trimmed).Trim();
                string description = colon >= 0 ? trimmed.Substring(colon + 1).Trim() : "";
                if (id.Length > 0 && seen.Add(id))
                {
                    goals.Add(new DecisionActionDefinition(id, description));
                }
            }

            return goals.Count == 0 ? null : new DecisionActionSet(goals);
        }
    }

    /// <summary>
    /// The local player's autopilot in an Rbx world: an <see cref="AutopilotController"/> over the player's Humanoid,
    /// the state of what the character sees, and the hand-back on real input. Each <see cref="Tick"/> (or each
    /// Heartbeat of an attached world scheduler):
    /// <list type="number">
    /// <item>follows a mod's request through the Player's <c>Autopilot</c> attribute;</item>
    /// <item>while driving, ends the autopilot at once when the player presses a movement key, clicks the world or
    /// moves the stick (input the chat or the Hub holds does not count), or when the character is gone;</item>
    /// <item>lets the policy ask the decision model when its cadence says so.</item>
    /// </list>
    /// Game rules stay in the world: goals C# does not know (gather, build) are published as
    /// <c>AutopilotGoal</c> for the mod to carry out. Engine-free; call from the game thread.
    /// </summary>
    public sealed class RbxCharacterAutopilot : IDisposable
    {
        /// <summary>Release reason when a mod set <c>Autopilot = false</c>.</summary>
        public const string ReleasedByWorld = "world";

        /// <summary>Release reason when the character died or despawned.</summary>
        public const string ReleasedCharacterLost = "character_lost";

        /// <summary>Release reason when another world was loaded.</summary>
        public const string ReleasedWorldChanged = "world_changed";

        private readonly RbxCharacterAutopilotOptions _options;
        private readonly RbxPlayerInputWatcher _input;
        private readonly RbxHeartbeatLinks _heartbeat;
        private RbxPlayer _player;
        private bool _published;
        private bool _disposed;

        /// <summary>Creates a disengaged autopilot.</summary>
        /// <param name="world">The live world.</param>
        /// <param name="client">Normally <c>provider.ForConsumer(DecisionConsumers.Autopilot)</c>.</param>
        /// <param name="clock">The decision clock (the player-loop clock in Unity).</param>
        /// <param name="trace">Receives every decision; null discards them.</param>
        /// <param name="router">Named in the status (which model, or why none); null leaves it out.</param>
        /// <param name="options">Goals, actions, input and state settings; null uses the defaults.</param>
        /// <param name="fallback">Decides when the model cannot; null keeps the current goal, else <see cref="RbxCharacterAutopilotOptions.DefaultGoalId"/>.</param>
        public RbxCharacterAutopilot(IRbxCharacterWorld world, IDecisionClient client, IDecisionClock clock,
            IDecisionTraceSink trace = null, IDecisionRouter router = null, RbxCharacterAutopilotOptions options = null,
            IAutopilotFallbackPolicy fallback = null)
        {
            _options = options ?? new RbxCharacterAutopilotOptions();
            Subject = RbxCharacterSubject.LocalPlayer(world ?? throw new ArgumentNullException(nameof(world)),
                _options.LocalActorId);
            Actuator = new RbxHumanoidAutopilotActuator(Subject, _options.Actions, _options.UseWorldAttributes,
                _options.Seed);
            State = new RbxCharacterStateProvider(Subject, _options.State, () => Actuator.CurrentGoalId);
            _input = new RbxPlayerInputWatcher(_options.Input);
            Controller = new AutopilotController(_options.Policy, client, State, Actuator, clock, trace,
                fallback ?? new KeepGoalAutopilotFallbackPolicy(_options.DefaultGoalId), router, ResolveGoals);
            Controller.EngagedChanged += HandleEngagedChanged;
            _heartbeat = new RbxHeartbeatLinks(Tick);
        }

        /// <summary>The control the <c>/autopilot</c> command and a host's UI use.</summary>
        public AutopilotController Controller { get; }

        /// <summary>The character driven.</summary>
        public RbxCharacterSubject Subject { get; }

        /// <summary>The actuator over the player's Humanoid.</summary>
        public RbxHumanoidAutopilotActuator Actuator { get; }

        /// <summary>The state text provider.</summary>
        public RbxCharacterStateProvider State { get; }

        /// <summary>Call once per frame (or attach a scheduler); cheap when nothing is due.</summary>
        public void Tick()
        {
            if (_disposed)
            {
                return;
            }

            RbxPlayer player = Subject.FindLocalPlayer();
            if (!ReferenceEquals(player, _player))
            {
                SwitchPlayer(player);
            }

            if (_options.UseWorldAttributes && player != null)
            {
                FollowWorldRequest(player);
            }

            if (!Controller.Engaged)
            {
                return;
            }

            if (_input.HasPlayerInput(Subject.World.Game))
            {
                Controller.NotifyPlayerInput();
                return;
            }

            if (!Actuator.CanDrive(out string _))
            {
                Controller.Disengage(ReleasedCharacterLost);
                return;
            }

            Task tick = Controller.Tick();
            if (tick.IsFaulted)
            {
                _ = tick.Exception;
            }
        }

        /// <summary>
        /// Ticks on every Heartbeat of <paramref name="scheduler"/>, the world's own frame, so the autopilot pauses
        /// with the world. Attach each world as it is built: only the published world's scheduler advances, and a
        /// replaced world is not kept alive by this attachment. Dispose the handle to stop.
        /// </summary>
        public IDisposable AttachScheduler(ModScheduler scheduler)
        {
            return _heartbeat.Attach(scheduler);
        }

        /// <summary>Hands control back, stops ticking and leaves every scheduler.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _heartbeat.Dispose();
            Controller.Dispose();
            Controller.EngagedChanged -= HandleEngagedChanged;
            _disposed = true;
        }

        private void SwitchPlayer(RbxPlayer player)
        {
            // WHY only after a known player: the first tick of an autopilot engaged from the chat meets its player
            // for the first time, and that is not another world.
            if (_player != null && Controller.Engaged)
            {
                Controller.Disengage(ReleasedWorldChanged);
            }

            _player = player;
            _published = Controller.Engaged;
        }

        private void FollowWorldRequest(RbxPlayer player)
        {
            bool? requested = RbxCharacterQueries.BoolAttribute(player, RbxAgentAttributes.Autopilot);
            if (!requested.HasValue || requested.Value == _published)
            {
                return;
            }

            if (!requested.Value)
            {
                if (!Controller.Disengage(ReleasedByWorld))
                {
                    _published = false;
                }

                return;
            }

            if (!Controller.Engage().Engaged)
            {
                Publish(false);
            }
        }

        private DecisionActionSet ResolveGoals()
        {
            if (!_options.UseWorldAttributes)
            {
                return null;
            }

            RbxPlayer player = _player ?? Subject.FindLocalPlayer();
            return RbxCharacterAutopilotOptions.ParseGoals(
                RbxCharacterQueries.StringAttribute(player, RbxAgentAttributes.AutopilotGoals));
        }

        private void HandleEngagedChanged(bool engaged, string reason)
        {
            Publish(engaged);
        }

        private void Publish(bool engaged)
        {
            _published = engaged;
            if (!_options.UseWorldAttributes)
            {
                return;
            }

            RbxPlayer player = _player ?? Subject.FindLocalPlayer();
            RbxCharacterQueries.TryWriteAttribute(player, RbxAgentAttributes.Autopilot, engaged);
        }
    }
}
