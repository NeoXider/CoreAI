using System.Text;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// Turns a persona and a heard line into the two texts of an NPC turn: the cache-safe request instructions
    /// (who the NPC is and how it talks) and the user line. Replace it to change how NPCs are prompted.
    /// </summary>
    public interface INpcTurnPromptBuilder
    {
        /// <summary>The request's system instructions (<c>AiTaskRequest.RequestSystemInstructions</c>).</summary>
        string BuildInstructions(NpcAgent agent, NpcHeardLine line);

        /// <summary>The user payload (<c>AiTaskRequest.Hint</c>) for the already length-capped <paramref name="text"/>.</summary>
        string BuildUserLine(NpcAgent agent, NpcHeardLine line, string text);
    }

    /// <summary>
    /// The default prompt: the NPC's name, its persona and voice rules, who it is talking to, and the rule to
    /// answer with one to three spoken sentences without a name prefix, stage directions or quotes.
    /// </summary>
    public sealed class NpcTurnPromptBuilder : INpcTurnPromptBuilder
    {
        /// <inheritdoc />
        public string BuildInstructions(NpcAgent agent, NpcHeardLine line)
        {
            string name = agent?.Name ?? "";
            StringBuilder sb = new(256);
            sb.Append("You are ").Append(name).Append(", a character in this world.");
            string persona = (agent?.Persona.PromptFragment ?? "").Trim();
            if (persona.Length > 0)
            {
                sb.Append(' ').Append(persona);
            }

            string voice = (agent?.Persona.VoiceRules ?? "").Trim();
            if (voice.Length > 0)
            {
                sb.Append("\nHow you talk: ").Append(voice);
            }

            string speaker = line?.EffectiveSpeakerName ?? "";
            if (speaker.Length > 0)
            {
                sb.Append("\nYou are talking with ").Append(speaker).Append('.');
            }

            sb.Append("\nAnswer as ").Append(name)
                .Append(" with one to three short spoken sentences: no name prefix, no stage directions, no quotes, ")
                .Append("and never say you are an AI.");
            return sb.ToString();
        }

        /// <inheritdoc />
        public string BuildUserLine(NpcAgent agent, NpcHeardLine line, string text)
        {
            string speaker = line?.EffectiveSpeakerName ?? "";
            return speaker.Length > 0 ? speaker + ": " + (text ?? "") : text ?? "";
        }
    }
}
