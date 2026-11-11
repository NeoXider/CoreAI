namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// When an NPC considers a heard line meant for it, as data. Distances are in the game's own world units
    /// and are enforced only when both the NPC's and the speaker's positions are known: a chat-only game that
    /// supplies no positions still gets NPCs that answer to their names.
    /// </summary>
    public sealed class NpcAddressingRules
    {
        /// <summary>Answer a chat line that names the NPC (display name or alias, whole words, any case).</summary>
        public bool AnswerToName { get; set; } = true;

        /// <summary>Farthest a speaker may be for a named line to reach the NPC; 0 or less is unlimited.</summary>
        public double HearingRadius { get; set; } = 24d;

        /// <summary>Require an unobstructed line between speaker and NPC for chat lines (needs a line-of-sight query).</summary>
        public bool RequireLineOfSight { get; set; }

        /// <summary>Farthest a speaker may be for an interaction (a click, a key press on the NPC); 0 or less is unlimited.</summary>
        public double InteractionRadius { get; set; } = 8d;

        /// <summary>
        /// Seconds after the NPC answered a speaker during which that speaker's next unnamed line still goes to
        /// the NPC, so a conversation does not need the name in every line; 0 or less turns follow-ups off. When
        /// both positions are known the follow-up also needs the speaker within <see cref="HearingRadius"/>, so
        /// walking away ends the conversation; without positions only the time counts.
        /// </summary>
        public double FollowUpSeconds { get; set; } = 20d;

        /// <summary>
        /// Distance within which the NPC answers a line that names nobody (the nearest such NPC wins); 0 or less
        /// (the default) answers only lines addressed to it.
        /// </summary>
        public double AnswerUnaddressedWithin { get; set; }

        /// <summary>A copy that a later edit of this instance does not change.</summary>
        public NpcAddressingRules Clone()
        {
            return (NpcAddressingRules)MemberwiseClone();
        }
    }

    /// <summary>
    /// What one NPC may spend, as data. The world-wide limits (calls per minute, concurrent turns) live on
    /// <see cref="NpcDirectorOptions"/>.
    /// </summary>
    public sealed class NpcBudget
    {
        /// <summary>
        /// Least time between two model turns of this NPC, in seconds. A line that arrives sooner is not dropped:
        /// it waits, and a newer line replaces it, so the NPC answers the latest thing it heard.
        /// </summary>
        public double CooldownSeconds { get; set; } = 1.5d;

        /// <summary>Response token cap of one turn; 0 or less inherits the role and endpoint defaults.</summary>
        public int MaxOutputTokens { get; set; } = 160;

        /// <summary>
        /// Queue priority of this NPC's turns. Higher runs first and the player's own agents run at 0, so the
        /// negative default keeps NPC chatter behind the player's build work.
        /// </summary>
        public int QueuePriority { get; set; } = -10;

        /// <summary>Longest heard line passed to the model, in characters; longer lines are cut.</summary>
        public int MaxHeardCharacters { get; set; } = 400;

        /// <summary>Longest reply shown, in characters; a longer reply is cut at a word boundary.</summary>
        public int MaxReplyCharacters { get; set; } = 400;

        /// <summary>A copy that a later edit of this instance does not change.</summary>
        public NpcBudget Clone()
        {
            return (NpcBudget)MemberwiseClone();
        }
    }
}
