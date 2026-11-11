using System;
using System.Collections.Generic;
using CoreAI.Decisions;
using CoreAI.Logging;

namespace CoreAI.Ai.Npc
{
    /// <summary>Event kinds the NPC block reports into an NPC's decision policy.</summary>
    public static class NpcDialogueEventKinds
    {
        /// <summary>Someone spoke to the NPC (by name, as a follow-up, through an interaction or by proximity).</summary>
        public const string SpokenTo = "spoken_to";
    }

    /// <summary>The one an NPC's own talk is aimed at: a player, or another character.</summary>
    public readonly struct NpcDialoguePartner
    {
        /// <summary>Creates a partner; an empty id becomes <c>player</c>.</summary>
        public NpcDialoguePartner(string id, string name, NpcPosition? position = null)
        {
            Id = string.IsNullOrWhiteSpace(id) ? "player" : id.Trim();
            Name = (name ?? "").Trim();
            Position = position;
        }

        /// <summary>Stable speaker id; the NPC keeps its memory of the conversation under it.</summary>
        public string Id { get; }

        /// <summary>The name the NPC uses; empty uses the id.</summary>
        public string Name { get; }

        /// <summary>Where the partner stands; null when unknown.</summary>
        public NpcPosition? Position { get; }

        /// <summary>The name with the id fallback applied.</summary>
        public string EffectiveName => Name.Length > 0 ? Name : Id;
    }

    /// <summary>Which decisions are dialogue, and what they make the NPC say. Plain data.</summary>
    public sealed class NpcDialogueActuatorOptions
    {
        /// <summary>
        /// Actions answered by a model turn: the NPC is asked the stage direction as an interaction from the partner,
        /// so it speaks in its own voice with its own memory. <c>{partner}</c> is replaced with the partner's name.
        /// </summary>
        public IDictionary<string, string> AskPrompts { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["talk"] = "({partner} is standing next to you. Say one short thing to {partner}, in character.)"
        };

        /// <summary>
        /// Actions answered by a fixed line without the model, spoken in rotation; <c>{partner}</c> is replaced with
        /// the partner's name.
        /// </summary>
        public IDictionary<string, string[]> SayLines { get; set; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["greet"] = new[] { "Hello, {partner}!", "Good day, {partner}." }
        };

        /// <summary>True skips a dialogue action while the NPC is answering someone, so its decision never cuts a reply short.</summary>
        public bool SkipWhileSpeaking { get; set; } = true;
    }

    /// <summary>What <see cref="NpcDialogueActuator"/> did with the last decision.</summary>
    public enum NpcDialogueOutcome
    {
        /// <summary>The action is not a dialogue action.</summary>
        NotDialogue = 0,

        /// <summary>A model turn was started (<see cref="INpcDirector.Ask"/>).</summary>
        Asked = 1,

        /// <summary>A fixed line was spoken (<see cref="INpcDirector.Say"/>).</summary>
        Said = 2,

        /// <summary>Skipped: the NPC was answering someone.</summary>
        SkippedBusy = 3,

        /// <summary>Skipped: the director does not know the NPC, or refused the line.</summary>
        Refused = 4
    }

    /// <summary>
    /// Carries out the dialogue decisions of an NPC brain: a <c>talk</c> decision starts a model turn through
    /// <see cref="INpcDirector.Ask"/> (budgets apply), a <c>greet</c> decision says a fixed line through
    /// <see cref="INpcDirector.Say"/> (no model). Other actions are left to the other actuators of the NPC (combine
    /// them with <see cref="CompositeNpcActuator"/>).
    /// </summary>
    public sealed class NpcDialogueActuator : INpcActuator
    {
        private readonly INpcDirector _director;
        private readonly NpcDialogueActuatorOptions _options;
        private readonly Func<NpcDialoguePartner?> _partner;
        private readonly Dictionary<string, int> _rotation = new(StringComparer.Ordinal);

        /// <summary>Creates the actuator.</summary>
        /// <param name="director">The director the NPC lives in.</param>
        /// <param name="options">Which actions are dialogue; null uses the defaults (<c>talk</c>, <c>greet</c>).</param>
        /// <param name="partner">Whom the NPC talks to now; null, or a null answer, means a generic player.</param>
        public NpcDialogueActuator(INpcDirector director, NpcDialogueActuatorOptions options = null,
            Func<NpcDialoguePartner?> partner = null)
        {
            _director = director ?? throw new ArgumentNullException(nameof(director));
            _options = options ?? new NpcDialogueActuatorOptions();
            _partner = partner;
        }

        /// <summary>What the last <see cref="Apply"/> did.</summary>
        public NpcDialogueOutcome LastOutcome { get; private set; }

        /// <summary>True when <paramref name="actionId"/> is one of this actuator's dialogue actions.</summary>
        public bool Handles(string actionId)
        {
            string id = actionId ?? "";
            return (_options.AskPrompts != null && _options.AskPrompts.ContainsKey(id)) ||
                   (_options.SayLines != null && _options.SayLines.ContainsKey(id));
        }

        /// <inheritdoc />
        public void Apply(NpcDecision decision)
        {
            LastOutcome = Run(decision);
        }

        private NpcDialogueOutcome Run(NpcDecision decision)
        {
            if (decision == null || !Handles(decision.ActionId))
            {
                return NpcDialogueOutcome.NotDialogue;
            }

            if (!_director.TryGet(decision.NpcId, out NpcAgent agent))
            {
                return NpcDialogueOutcome.Refused;
            }

            if (_options.SkipWhileSpeaking && (agent.IsSpeaking || agent.HasPendingLine))
            {
                return NpcDialogueOutcome.SkippedBusy;
            }

            NpcDialoguePartner partner = ResolvePartner();
            if (_options.AskPrompts != null && _options.AskPrompts.TryGetValue(decision.ActionId, out string prompt))
            {
                NpcHeardLine line = NpcHeardLine.Interaction(agent.Id, partner.Id, partner.EffectiveName,
                    Fill(prompt, partner), partner.Position);
                return _director.Ask(agent.Id, line).IsTaken ? NpcDialogueOutcome.Asked : NpcDialogueOutcome.Refused;
            }

            string[] lines = _options.SayLines[decision.ActionId];
            if (lines == null || lines.Length == 0)
            {
                return NpcDialogueOutcome.Refused;
            }

            _rotation.TryGetValue(decision.ActionId, out int next);
            _rotation[decision.ActionId] = next + 1;
            string text = Fill(lines[next % lines.Length], partner);
            return _director.Say(agent.Id, text) ? NpcDialogueOutcome.Said : NpcDialogueOutcome.Refused;
        }

        private NpcDialoguePartner ResolvePartner()
        {
            NpcDialoguePartner? partner = null;
            try
            {
                partner = _partner?.Invoke();
            }
            catch (Exception)
            {
                partner = null;
            }

            return partner ?? new NpcDialoguePartner("player", "");
        }

        private static string Fill(string template, NpcDialoguePartner partner)
        {
            string name = partner.Name.Length > 0 ? partner.Name : "the traveller";
            return (template ?? "").Replace("{partner}", name);
        }
    }

    /// <summary>
    /// Several actuators for one NPC, applied in order (movement, then dialogue). One that throws is logged and
    /// skipped, so a broken body never stops the NPC's speech, and the policy's tick never faults.
    /// </summary>
    public sealed class CompositeNpcActuator : INpcActuator
    {
        private readonly List<INpcActuator> _actuators = new();
        private readonly ILog _log;

        /// <summary>Creates the composite; null entries are ignored.</summary>
        public CompositeNpcActuator(IEnumerable<INpcActuator> actuators, ILog log = null)
        {
            if (actuators != null)
            {
                foreach (INpcActuator actuator in actuators)
                {
                    if (actuator != null)
                    {
                        _actuators.Add(actuator);
                    }
                }
            }

            _log = log ?? NullLog.Instance;
        }

        /// <summary>The actuators, in order.</summary>
        public IReadOnlyList<INpcActuator> Actuators => _actuators;

        /// <inheritdoc />
        public void Apply(NpcDecision decision)
        {
            foreach (INpcActuator actuator in _actuators)
            {
                try
                {
                    actuator.Apply(decision);
                }
                catch (Exception ex)
                {
                    _log.Warn($"[Npc] Actuator {actuator.GetType().Name} threw on '{decision?.ActionId}': " +
                              $"{ex.GetType().Name}: {ex.Message}", LogTag.Llm);
                }
            }
        }
    }

    /// <summary>
    /// Feeds the director's <see cref="INpcDirector.NpcAddressed"/> events into the decision policies of the NPCs
    /// that were spoken to, as <see cref="NpcDialogueEventKinds.SpokenTo"/> events, so an NPC brain can decide to
    /// turn to the speaker, stop walking or greet back. Call from the thread that drives the director and the
    /// policies (the game thread).
    /// </summary>
    public sealed class NpcAddressedDecisionFeed : IDisposable
    {
        private readonly INpcDirector _director;
        private readonly Func<NpcAddressed, NpcDecisionEvent> _map;
        private readonly Dictionary<string, NpcDecisionPolicy> _policies = new(StringComparer.Ordinal);
        private bool _disposed;

        /// <summary>Subscribes to <paramref name="director"/> at once.</summary>
        /// <param name="director">The director whose events are fed.</param>
        /// <param name="map">Turns one event into a decision event; null uses <see cref="DefaultEvent"/>. A null answer skips the event.</param>
        public NpcAddressedDecisionFeed(INpcDirector director, Func<NpcAddressed, NpcDecisionEvent> map = null)
        {
            _director = director ?? throw new ArgumentNullException(nameof(director));
            _map = map ?? DefaultEvent;
            _director.NpcAddressed += HandleAddressed;
        }

        /// <summary>Events fed so far.</summary>
        public int FedCount { get; private set; }

        /// <summary>The policy fed for <paramref name="npcId"/>; replaces an earlier one. Dispose the handle to stop.</summary>
        public IDisposable Attach(string npcId, NpcDecisionPolicy policy)
        {
            string id = (npcId ?? "").Trim();
            if (id.Length == 0)
            {
                throw new ArgumentException("A decision feed needs a non-empty NPC id.", nameof(npcId));
            }

            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            _policies[id] = policy;
            return new Attachment(this, id, policy);
        }

        /// <summary>The default mapping: <c>spoken_to</c> with who spoke, how, and the line.</summary>
        public static NpcDecisionEvent DefaultEvent(NpcAddressed addressed)
        {
            string speaker = addressed.SpeakerName.Length > 0 ? addressed.SpeakerName : addressed.SpeakerId;
            string how;
            switch (addressed.Reason)
            {
                case NpcAddressReason.Name:
                    how = " spoke to you by name";
                    break;
                case NpcAddressReason.FollowUp:
                    how = " went on talking to you";
                    break;
                case NpcAddressReason.Interaction:
                    how = " turned to you";
                    break;
                case NpcAddressReason.Proximity:
                    how = " said something next to you";
                    break;
                default:
                    how = " spoke to you";
                    break;
            }

            return new NpcDecisionEvent(NpcDialogueEventKinds.SpokenTo,
                speaker + how + ": \"" + NpcText.Clamp(addressed.Text, 200) + "\"");
        }

        /// <summary>Stops feeding every policy; the policies themselves stay alive.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _director.NpcAddressed -= HandleAddressed;
            _policies.Clear();
        }

        private void HandleAddressed(NpcAddressed addressed)
        {
            if (_disposed || !_policies.TryGetValue(addressed.NpcId, out NpcDecisionPolicy policy))
            {
                return;
            }

            NpcDecisionEvent decisionEvent = _map(addressed);
            if (decisionEvent == null)
            {
                return;
            }

            policy.Notify(decisionEvent);
            FedCount++;
        }

        private sealed class Attachment : IDisposable
        {
            private NpcAddressedDecisionFeed _owner;
            private readonly string _npcId;
            private readonly NpcDecisionPolicy _policy;

            public Attachment(NpcAddressedDecisionFeed owner, string npcId, NpcDecisionPolicy policy)
            {
                _owner = owner;
                _npcId = npcId;
                _policy = policy;
            }

            public void Dispose()
            {
                NpcAddressedDecisionFeed owner = _owner;
                _owner = null;
                if (owner != null && owner._policies.TryGetValue(_npcId, out NpcDecisionPolicy current) &&
                    ReferenceEquals(current, _policy))
                {
                    owner._policies.Remove(_npcId);
                }
            }
        }
    }
}
