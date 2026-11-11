using System;
using System.Threading;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// One NPC held by an <see cref="INpcDirector"/>: its persona, rules and budget (copied when it was added),
    /// its memory key and origin, and its conversation state. The director owns the turn machinery; this type
    /// is the read-only view a game, a quest rule or a script inspects.
    /// </summary>
    public sealed class NpcAgent
    {
        private readonly Func<NpcPosition?> _positionSource;
        private int _fallbackRotation;

        internal NpcAgent(NpcAgentDefinition definition, string defaultSourceTag)
        {
            if (definition?.Persona == null)
            {
                throw new ArgumentNullException(nameof(definition), "An NPC needs a persona.");
            }

            Persona = definition.Persona.Clone();
            Id = (Persona.Id ?? "").Trim();
            if (Id.Length == 0)
            {
                throw new ArgumentException("An NPC persona needs a non-empty Id.", nameof(definition));
            }

            Persona.Id = Id;
            Addressing = (definition.Addressing ?? new NpcAddressingRules()).Clone();
            Budget = (definition.Budget ?? new NpcBudget()).Clone();
            MemoryKey = string.IsNullOrWhiteSpace(definition.MemoryKey) ? Id : definition.MemoryKey.Trim();
            OriginTag = (definition.OriginTag ?? "").Trim();
            SourceTag = string.IsNullOrWhiteSpace(definition.SourceTag)
                ? (defaultSourceTag ?? "")
                : definition.SourceTag.Trim();
            _positionSource = definition.PositionSource;
            Names = Persona.NamesToAnswer();
        }

        /// <summary>Stable id (the persona id).</summary>
        public string Id { get; }

        /// <summary>Display name.</summary>
        public string Name => Persona.EffectiveName;

        /// <summary>A private copy of the persona the NPC was added with.</summary>
        public NpcPersona Persona { get; }

        /// <summary>A private copy of its addressing rules.</summary>
        public NpcAddressingRules Addressing { get; }

        /// <summary>A private copy of its budget.</summary>
        public NpcBudget Budget { get; }

        /// <summary>Key of its memory partition; the memory topic is <c>npc:&lt;MemoryKey&gt;</c>.</summary>
        public string MemoryKey { get; }

        /// <summary>Origin tag (<c>ai:&lt;modId&gt;</c> for an agent a mod created); empty for a host NPC.</summary>
        public string OriginTag { get; }

        /// <summary><c>AiTaskRequest.SourceTag</c> of its turns.</summary>
        public string SourceTag { get; }

        /// <summary>The names it answers to in chat.</summary>
        public System.Collections.Generic.IReadOnlyList<string> Names { get; }

        /// <summary>True while one of its model turns is running.</summary>
        public bool IsSpeaking => ActiveRun != null;

        /// <summary>True while a heard line waits for its cooldown or for a free turn slot.</summary>
        public bool HasPendingLine => Pending != null;

        /// <summary>The speaker it last answered; empty before its first answer.</summary>
        public string ConversationPartnerId { get; internal set; } = "";

        /// <summary>When it last answered <see cref="ConversationPartnerId"/>, on the director's clock.</summary>
        public double ConversationAtSeconds { get; internal set; } = double.NegativeInfinity;

        /// <summary>True once the director removed it.</summary>
        public bool IsRemoved { get; internal set; }

        /// <summary>Where it stands now; null when it has no position source or the source failed.</summary>
        public NpcPosition? Position
        {
            get
            {
                if (_positionSource == null || IsRemoved)
                {
                    return null;
                }

                try
                {
                    return _positionSource();
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        internal NpcTurnRun ActiveRun { get; set; }

        internal NpcPendingTurn Pending { get; set; }

        internal double LastTurnStartedAt { get; set; } = double.NegativeInfinity;

        internal string NextFallbackLine(NpcDirectorOptions options)
        {
            System.Collections.Generic.IReadOnlyList<string> lines = Persona.FallbackLines.Count > 0
                ? Persona.FallbackLines
                : options?.DefaultFallbackLines ?? Array.Empty<string>();
            for (int attempt = 0; attempt < lines.Count; attempt++)
            {
                string line = (lines[_fallbackRotation++ % lines.Count] ?? "").Trim();
                if (line.Length > 0)
                {
                    return line;
                }
            }

            string lastResort = (options?.LastResortLine ?? "").Trim();
            return lastResort.Length > 0 ? lastResort : "...";
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "NpcAgent(" + Id + ")";
        }
    }

    /// <summary>A heard line waiting to become a model turn.</summary>
    internal sealed class NpcPendingTurn
    {
        public NpcPendingTurn(NpcHeardLine line, string text, long turnId, NpcAddressReason reason, double requestedAt)
        {
            Line = line;
            Text = text;
            TurnId = turnId;
            Reason = reason;
            RequestedAt = requestedAt;
        }

        public NpcHeardLine Line { get; }

        public string Text { get; }

        public long TurnId { get; }

        public NpcAddressReason Reason { get; }

        public double RequestedAt { get; }
    }

    /// <summary>One running model turn.</summary>
    internal sealed class NpcTurnRun
    {
        public NpcTurnRun(NpcAgent agent, NpcPendingTurn pending, NpcSpeech speech, CancellationTokenSource cancellation,
            double startedAt)
        {
            Agent = agent;
            Pending = pending;
            Speech = speech;
            Cancellation = cancellation;
            StartedAt = startedAt;
        }

        public NpcAgent Agent { get; }

        public NpcPendingTurn Pending { get; }

        public NpcSpeech Speech { get; }

        public CancellationTokenSource Cancellation { get; }

        public double StartedAt { get; }

        public bool Ended { get; set; }
    }
}
