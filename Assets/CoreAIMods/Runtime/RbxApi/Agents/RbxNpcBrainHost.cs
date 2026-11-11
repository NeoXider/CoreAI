using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai.Npc;
using CoreAI.Decisions;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;

namespace CoreAI.Mods.Rbx.Agents
{
    /// <summary>
    /// The attributes a mod puts on a villager's Model to give it a decision brain; the brain itself is C#
    /// (<see cref="RbxNpcBrain"/>), the choice of villagers and actions stays in the mod.
    /// </summary>
    public static class RbxNpcBrainAttributes
    {
        /// <summary>String: the id of the villager's agent in the NPC director (<c>agent.Id</c> in Lua).</summary>
        public const string Agent = "NpcAgent";

        /// <summary>
        /// String: the actions its brain chooses from, in the goal-list format of
        /// <see cref="RbxAgentAttributes.AutopilotGoals"/> (<c>idle, wander, greet, face_player</c>). Removing the
        /// attribute removes the brain.
        /// </summary>
        public const string Actions = "NpcBrain";
    }

    /// <summary>Settings shared by every brain a host builds. Plain data.</summary>
    public sealed class RbxNpcBrainHostOptions
    {
        /// <summary>How often the Workspace is searched for villagers that gained or lost a brain, in seconds.</summary>
        public double ScanSeconds { get; set; } = 1d;

        /// <summary>At most this many brains run at once; villagers beyond it get none.</summary>
        public int MaxBrains { get; set; } = 16;

        /// <summary>The template for each brain; its actions are replaced by the villager's own list.</summary>
        public Func<RbxNpcBrainOptions> BrainOptions { get; set; } = () => new RbxNpcBrainOptions();
    }

    /// <summary>
    /// Gives a decision brain to every villager a mod marks with <see cref="RbxNpcBrainAttributes"/>: a Model directly
    /// under the Workspace with <c>NpcAgent</c> (an agent the director knows) and <c>NpcBrain</c> (its actions).
    /// Brains are created and removed as the mod marks, unmarks or destroys villagers, and ticked on every
    /// <see cref="Tick"/> (or every Heartbeat of an attached world scheduler). Engine-free; call from the game thread.
    /// </summary>
    public sealed class RbxNpcBrainHost : IDisposable
    {
        private readonly IRbxCharacterWorld _world;
        private readonly INpcDirector _director;
        private readonly IDecisionClient _client;
        private readonly IDecisionClock _clock;
        private readonly IDecisionTraceSink _trace;
        private readonly RbxNpcBrainHostOptions _options;
        private readonly Dictionary<RbxInstance, Entry> _brains = new();
        private readonly RbxHeartbeatLinks _heartbeat;
        private double _nextScanAt = double.NegativeInfinity;
        private bool _disposed;

        /// <summary>Creates the host.</summary>
        /// <param name="world">The live world.</param>
        /// <param name="director">The NPCs the brains speak through.</param>
        /// <param name="client">Normally <c>provider.ForConsumer(DecisionConsumers.NpcBrain)</c>.</param>
        /// <param name="clock">The decision clock.</param>
        /// <param name="trace">Receives every decision; null discards them.</param>
        /// <param name="options">Scan cadence, the brain cap and the brain template; null uses the defaults.</param>
        public RbxNpcBrainHost(IRbxCharacterWorld world, INpcDirector director, IDecisionClient client,
            IDecisionClock clock, IDecisionTraceSink trace = null, RbxNpcBrainHostOptions options = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _director = director ?? throw new ArgumentNullException(nameof(director));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _trace = trace;
            _options = options ?? new RbxNpcBrainHostOptions();
            _heartbeat = new RbxHeartbeatLinks(Tick);
        }

        /// <summary>Brains running now.</summary>
        public int Count => _brains.Count;

        /// <summary>The brain of the villager whose agent id is <paramref name="npcId"/>; false when it has none.</summary>
        public bool TryGetBrain(string npcId, out RbxNpcBrain brain)
        {
            foreach (Entry entry in _brains.Values)
            {
                if (string.Equals(entry.Brain.NpcId, npcId, StringComparison.Ordinal))
                {
                    brain = entry.Brain;
                    return true;
                }
            }

            brain = null;
            return false;
        }

        /// <summary>Rescans when due and ticks every brain; cheap when nothing is due.</summary>
        public void Tick()
        {
            if (_disposed)
            {
                return;
            }

            double now = _clock.NowSeconds;
            if (now >= _nextScanAt)
            {
                _nextScanAt = now + Math.Max(0.1d, _options.ScanSeconds);
                Scan();
            }

            foreach (Entry entry in new List<Entry>(_brains.Values))
            {
                Task tick = entry.Brain.Tick();
                if (tick.IsFaulted)
                {
                    _ = tick.Exception;
                }
            }
        }

        /// <summary>Ticks on every Heartbeat of <paramref name="scheduler"/>; see <see cref="RbxHeartbeatLinks"/>.</summary>
        public IDisposable AttachScheduler(ModScheduler scheduler)
        {
            return _heartbeat.Attach(scheduler);
        }

        /// <summary>Stops every brain and leaves every scheduler.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _heartbeat.Dispose();
            foreach (Entry entry in _brains.Values)
            {
                entry.Brain.Dispose();
            }

            _brains.Clear();
        }

        private void Scan()
        {
            List<(RbxInstance Model, string NpcId, string Actions)> marked = new();
            RbxInstance workspace = RbxCharacterQueries.Workspace(_world.Game);
            if (workspace != null)
            {
                foreach (RbxInstance child in workspace.GetChildren())
                {
                    if (child == null || child.IsDestroyed || !child.IsA("Model"))
                    {
                        continue;
                    }

                    string npcId = RbxCharacterQueries.StringAttribute(child, RbxNpcBrainAttributes.Agent).Trim();
                    string actions = RbxCharacterQueries.StringAttribute(child, RbxNpcBrainAttributes.Actions).Trim();
                    if (npcId.Length > 0 && actions.Length > 0 && _director.TryGet(npcId, out NpcAgent _))
                    {
                        marked.Add((child, npcId, actions));
                    }
                }
            }

            HashSet<RbxInstance> current = new();
            foreach ((RbxInstance model, string npcId, string actions) in marked)
            {
                if (_brains.TryGetValue(model, out Entry existing) &&
                    string.Equals(existing.NpcId, npcId, StringComparison.Ordinal) &&
                    string.Equals(existing.Actions, actions, StringComparison.Ordinal))
                {
                    current.Add(model);
                }
            }

            // WHY stale brains go before new ones are added: a slot freed by a destroyed villager must be
            // available to the next one within the same scan.
            foreach (RbxInstance stale in new List<RbxInstance>(_brains.Keys))
            {
                if (!current.Contains(stale))
                {
                    Remove(stale);
                }
            }

            foreach ((RbxInstance model, string npcId, string actions) in marked)
            {
                if (!_brains.ContainsKey(model) && _brains.Count < Math.Max(0, _options.MaxBrains))
                {
                    Add(model, npcId, actions);
                }
            }
        }

        private void Add(RbxInstance model, string npcId, string actions)
        {
            DecisionActionSet set = RbxCharacterAutopilotOptions.ParseGoals(actions);
            if (set == null || set.Validate().Count > 0)
            {
                return;
            }

            RbxNpcBrainOptions options = _options.BrainOptions?.Invoke() ?? new RbxNpcBrainOptions();
            options.Actions = set;
            if (!set.Contains(options.DefaultActionId))
            {
                options.DefaultActionId = set.Actions[0].Id;
            }

            try
            {
                RbxNpcBrain brain = new(npcId, RbxCharacterSubject.Of(_world, model), _director, _client, _clock,
                    _trace, options);
                _brains[model] = new Entry(npcId, actions, brain);
            }
            catch (ArgumentException)
            {
                // WHY swallowed: a malformed list written by a mod must not stop the other villagers' brains; the
                // villager simply has none until the mod writes a valid one.
            }
        }

        private void Remove(RbxInstance model)
        {
            if (_brains.TryGetValue(model, out Entry entry))
            {
                _brains.Remove(model);
                entry.Brain.Dispose();
            }
        }

        private sealed class Entry
        {
            public Entry(string npcId, string actions, RbxNpcBrain brain)
            {
                NpcId = npcId;
                Actions = actions;
                Brain = brain;
            }

            public string NpcId { get; }

            public string Actions { get; }

            public RbxNpcBrain Brain { get; }
        }
    }
}
