using System;
using System.Collections.Generic;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// Recognises a chat line addressed to the player's own AI, which no villager may take: the line starts (after
    /// spaces and an optional <c>@</c>) with one of the address aliases or with the AI's display name, in any case,
    /// followed by the end of the line, a space or one of <c>, : ; . ! ? -</c>. An alias always addresses the AI;
    /// the display name yields to a villager who answers to the same name, so a host that clears the aliases can
    /// name a villager like its AI.
    /// </summary>
    public static class NpcAiAddress
    {
        private const string Separators = ",:;.!?-";

        /// <summary>The default address aliases: <c>ai</c> (so <c>ai, …</c> and <c>@ai …</c> reach the AI).</summary>
        public static IReadOnlyList<string> DefaultAliases { get; } = Array.AsReadOnly(new[] { "ai" });

        /// <summary>
        /// True when <paramref name="text"/> addresses the AI through one of <paramref name="aliases"/>, or through
        /// <paramref name="aiDisplayName"/> unless an NPC of <paramref name="villagers"/> answers to that name.
        /// </summary>
        public static bool IsAddressedToAi(string text, IReadOnlyList<string> aliases, string aiDisplayName,
            INpcDirector villagers = null)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (aliases != null)
            {
                for (int index = 0; index < aliases.Count; index++)
                {
                    if (StartsWithAddress(text, aliases[index]))
                    {
                        return true;
                    }
                }
            }

            return StartsWithAddress(text, aiDisplayName) && !AnyVillagerAnswersTo(villagers, aiDisplayName);
        }

        /// <summary>
        /// True when <paramref name="text"/> starts with <paramref name="name"/> (after spaces and an optional
        /// <c>@</c>, any case) followed by the end of the line, a space or a separator; false for an empty name.
        /// </summary>
        public static bool StartsWithAddress(string text, string name)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string address = name.Trim();
            int start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            if (start < text.Length && text[start] == '@')
            {
                start++;
            }

            if (text.Length - start < address.Length ||
                string.Compare(text, start, address, 0, address.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }

            int end = start + address.Length;
            return end == text.Length || char.IsWhiteSpace(text[end]) || Separators.IndexOf(text[end]) >= 0;
        }

        private static bool AnyVillagerAnswersTo(INpcDirector villagers, string name)
        {
            IReadOnlyList<NpcAgent> agents = villagers?.Agents;
            if (agents == null)
            {
                return false;
            }

            string wanted = (name ?? "").Trim();
            for (int index = 0; index < agents.Count; index++)
            {
                IReadOnlyList<string> names = agents[index]?.Names;
                if (names == null)
                {
                    continue;
                }

                for (int nameIndex = 0; nameIndex < names.Count; nameIndex++)
                {
                    if (string.Equals((names[nameIndex] ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
