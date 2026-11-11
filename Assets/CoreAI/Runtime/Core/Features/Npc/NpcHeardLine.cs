using CoreAI.Authority;

namespace CoreAI.Ai.Npc
{
    /// <summary>How a line reached the NPCs.</summary>
    public enum NpcHearingChannel
    {
        /// <summary>Said in the world chat: the addressing rules decide which NPC, if any, it is meant for.</summary>
        Chat = 0,

        /// <summary>Directed at one NPC (<see cref="NpcHeardLine.TargetNpcId"/>) by an interaction: a click, a key, a script.</summary>
        Interaction = 1
    }

    /// <summary>One line a speaker said where NPCs may hear it.</summary>
    public sealed class NpcHeardLine
    {
        /// <summary>Stable id of the speaker (a player id); NPCs remember a conversation per speaker id.</summary>
        public string SpeakerId { get; set; } = "";

        /// <summary>Name the NPC calls the speaker; empty uses <see cref="SpeakerId"/>.</summary>
        public string SpeakerName { get; set; } = "";

        /// <summary>What was said.</summary>
        public string Text { get; set; } = "";

        /// <summary>How the line reached the NPCs.</summary>
        public NpcHearingChannel Channel { get; set; } = NpcHearingChannel.Chat;

        /// <summary>For <see cref="NpcHearingChannel.Interaction"/>: the NPC the line is for.</summary>
        public string TargetNpcId { get; set; } = "";

        /// <summary>Where the speaker stood; null when the game does not track it (distance rules are then not enforced).</summary>
        public NpcPosition? SpeakerPosition { get; set; }

        /// <summary>
        /// The speaker's trusted actor, when the host has one: the NPC's memory and the queue's fairness then
        /// follow that actor. Null uses the host's identity provider, or the speaker id.
        /// </summary>
        public ActorContext? SpeakerActor { get; set; }

        /// <summary>The speaker name with the id fallback applied.</summary>
        public string EffectiveSpeakerName =>
            string.IsNullOrWhiteSpace(SpeakerName) ? (SpeakerId ?? "").Trim() : SpeakerName.Trim();

        /// <summary>A line said in chat.</summary>
        public static NpcHeardLine Chat(string speakerId, string speakerName, string text,
            NpcPosition? speakerPosition = null)
        {
            return new NpcHeardLine
            {
                SpeakerId = speakerId ?? "",
                SpeakerName = speakerName ?? "",
                Text = text ?? "",
                Channel = NpcHearingChannel.Chat,
                SpeakerPosition = speakerPosition
            };
        }

        /// <summary>A line directed at one NPC.</summary>
        public static NpcHeardLine Interaction(string npcId, string speakerId, string speakerName, string text,
            NpcPosition? speakerPosition = null)
        {
            return new NpcHeardLine
            {
                SpeakerId = speakerId ?? "",
                SpeakerName = speakerName ?? "",
                Text = text ?? "",
                Channel = NpcHearingChannel.Interaction,
                TargetNpcId = npcId ?? "",
                SpeakerPosition = speakerPosition
            };
        }
    }
}
