namespace CoreAI.Ai.Npc
{
    /// <summary>Why a heard line was routed to an NPC.</summary>
    public enum NpcAddressReason
    {
        /// <summary>No NPC took the line.</summary>
        None = 0,

        /// <summary>The line was directed at the NPC by an interaction or a script.</summary>
        Interaction = 1,

        /// <summary>The line named the NPC.</summary>
        Name = 2,

        /// <summary>The speaker was in a conversation with the NPC and named nobody else.</summary>
        FollowUp = 3,

        /// <summary>The line named nobody and the NPC was the nearest one answering unaddressed lines.</summary>
        Proximity = 4
    }

    /// <summary>Why a heard line produced no model turn.</summary>
    public enum NpcIgnoreReason
    {
        /// <summary>The line was taken.</summary>
        None = 0,

        /// <summary>The line was empty.</summary>
        EmptyLine = 1,

        /// <summary>The line named no NPC and no follow-up or proximity rule applied.</summary>
        NotAddressed = 2,

        /// <summary>The NPC the line was meant for was out of range.</summary>
        OutOfRange = 3,

        /// <summary>The NPC the line was meant for could not see the speaker.</summary>
        NoLineOfSight = 4,

        /// <summary>The line targeted an NPC id the director does not know.</summary>
        UnknownNpc = 5,

        /// <summary>The director was disposed.</summary>
        Disposed = 6
    }

    /// <summary>How one model turn of an NPC ended.</summary>
    public enum NpcTurnOutcome
    {
        /// <summary>The model answered and the answer was spoken.</summary>
        Replied = 0,

        /// <summary>The turn failed (or the world's budget was spent) and an in-character fallback line was spoken.</summary>
        FallbackLine = 1,

        /// <summary>A newer line to the same NPC replaced this turn; nothing was spoken for it.</summary>
        Superseded = 2,

        /// <summary>The NPC was removed or the director disposed; nothing was spoken.</summary>
        Cancelled = 3
    }

    /// <summary>An NPC took a heard line. Raised before its model turn starts.</summary>
    public readonly struct NpcAddressed
    {
        /// <summary>Creates the event.</summary>
        public NpcAddressed(string npcId, string npcName, string speakerId, string speakerName, string text,
            NpcAddressReason reason, long turnId, string originTag)
        {
            NpcId = npcId ?? "";
            NpcName = npcName ?? "";
            SpeakerId = speakerId ?? "";
            SpeakerName = speakerName ?? "";
            Text = text ?? "";
            Reason = reason;
            TurnId = turnId;
            OriginTag = originTag ?? "";
        }

        /// <summary>The NPC that took the line.</summary>
        public string NpcId { get; }

        /// <summary>Its display name.</summary>
        public string NpcName { get; }

        /// <summary>Who spoke.</summary>
        public string SpeakerId { get; }

        /// <summary>The speaker's name.</summary>
        public string SpeakerName { get; }

        /// <summary>What was said (after the length cap).</summary>
        public string Text { get; }

        /// <summary>Why this NPC took it.</summary>
        public NpcAddressReason Reason { get; }

        /// <summary>The turn that will answer it.</summary>
        public long TurnId { get; }

        /// <summary>Origin tag of the NPC (<c>ai:&lt;modId&gt;</c> for an agent a mod created); empty for a host NPC.</summary>
        public string OriginTag { get; }
    }

    /// <summary>An NPC spoke a line: a model reply, a fallback line, or a scripted line.</summary>
    public readonly struct NpcSaid
    {
        /// <summary>Creates the event.</summary>
        public NpcSaid(string npcId, string npcName, string speakerId, string speakerName, string text,
            bool isFallback, bool isScripted, long turnId, string originTag)
        {
            NpcId = npcId ?? "";
            NpcName = npcName ?? "";
            SpeakerId = speakerId ?? "";
            SpeakerName = speakerName ?? "";
            Text = text ?? "";
            IsFallback = isFallback;
            IsScripted = isScripted;
            TurnId = turnId;
            OriginTag = originTag ?? "";
        }

        /// <summary>The NPC that spoke.</summary>
        public string NpcId { get; }

        /// <summary>Its display name.</summary>
        public string NpcName { get; }

        /// <summary>Who it answered; empty for a scripted line said to nobody.</summary>
        public string SpeakerId { get; }

        /// <summary>The name of who it answered.</summary>
        public string SpeakerName { get; }

        /// <summary>The spoken line.</summary>
        public string Text { get; }

        /// <summary>True when the line is an in-character fallback, not a model reply.</summary>
        public bool IsFallback { get; }

        /// <summary>True when a script made the NPC say the line (no model involved).</summary>
        public bool IsScripted { get; }

        /// <summary>The turn that produced the line; 0 for a scripted line.</summary>
        public long TurnId { get; }

        /// <summary>Origin tag of the NPC; empty for a host NPC.</summary>
        public string OriginTag { get; }
    }

    /// <summary>A model turn of an NPC ended, whatever the outcome. Raised after <see cref="NpcSaid"/> when one was spoken.</summary>
    public readonly struct NpcTurnEnded
    {
        /// <summary>Creates the event.</summary>
        public NpcTurnEnded(string npcId, long turnId, NpcTurnOutcome outcome, string text, LlmErrorCode errorCode)
        {
            NpcId = npcId ?? "";
            TurnId = turnId;
            Outcome = outcome;
            Text = text ?? "";
            ErrorCode = errorCode;
        }

        /// <summary>The NPC whose turn ended.</summary>
        public string NpcId { get; }

        /// <summary>The turn.</summary>
        public long TurnId { get; }

        /// <summary>How it ended.</summary>
        public NpcTurnOutcome Outcome { get; }

        /// <summary>What the NPC said (the reply or the fallback line); empty when nothing was spoken.</summary>
        public string Text { get; }

        /// <summary>The failure category for <see cref="NpcTurnOutcome.FallbackLine"/>; for logs and rules, never shown to players.</summary>
        public LlmErrorCode ErrorCode { get; }
    }

    /// <summary>A heard line no NPC took.</summary>
    public readonly struct NpcLineIgnored
    {
        /// <summary>Creates the event.</summary>
        public NpcLineIgnored(string speakerId, string text, NpcIgnoreReason reason, string npcId)
        {
            SpeakerId = speakerId ?? "";
            Text = text ?? "";
            Reason = reason;
            NpcId = npcId ?? "";
        }

        /// <summary>Who spoke.</summary>
        public string SpeakerId { get; }

        /// <summary>What was said.</summary>
        public string Text { get; }

        /// <summary>Why no NPC took it.</summary>
        public NpcIgnoreReason Reason { get; }

        /// <summary>The NPC the line was meant for, when one was named but out of reach; otherwise empty.</summary>
        public string NpcId { get; }
    }
}
