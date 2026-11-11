using System;
using System.Collections.Generic;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// Who an NPC is, as data: its stable id, the names it answers to, the prompt fragment that makes it a
    /// person and the voice rules it speaks by, and the in-character lines it says when a model turn fails.
    /// One agent role (<see cref="BuiltInAgentRoleIds.AiNpc"/> by default) serves every persona; the persona
    /// travels in the request's cache-safe tail, so NPCs share the role's prompt prefix.
    /// </summary>
    public sealed class NpcPersona
    {
        private IReadOnlyList<string> _aliases = Array.Empty<string>();
        private IReadOnlyList<string> _fallbackLines = Array.Empty<string>();

        /// <summary>Stable id: the memory topic key, the cancellation scope and the key in the director.</summary>
        public string Id { get; set; } = "";

        /// <summary>The name shown over the NPC's head and in the chat; empty uses <see cref="Id"/>.</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>More names the NPC answers to in chat ("farmer", "old man").</summary>
        public IReadOnlyList<string> Aliases
        {
            get => _aliases;
            set => _aliases = value ?? Array.Empty<string>();
        }

        /// <summary>Who the NPC is: background, job, attitude, what it knows.</summary>
        public string PromptFragment { get; set; } = "";

        /// <summary>How the NPC talks: register, quirks, words it uses or avoids.</summary>
        public string VoiceRules { get; set; } = "";

        /// <summary>
        /// Lines the NPC says when its model turn fails or the world's budget is spent, picked in rotation.
        /// Empty uses <see cref="NpcDirectorOptions.DefaultFallbackLines"/>. Never provider text.
        /// </summary>
        public IReadOnlyList<string> FallbackLines
        {
            get => _fallbackLines;
            set => _fallbackLines = value ?? Array.Empty<string>();
        }

        /// <summary>Agent role that answers for this NPC; empty uses <see cref="BuiltInAgentRoleIds.AiNpc"/>.</summary>
        public string RoleId { get; set; } = BuiltInAgentRoleIds.AiNpc;

        /// <summary>Optional LLM routing profile for this NPC's turns; empty follows the role's routing.</summary>
        public string RoutingProfileId { get; set; } = "";

        /// <summary>The display name, or the id when no display name was set.</summary>
        public string EffectiveName => string.IsNullOrWhiteSpace(DisplayName) ? (Id ?? "").Trim() : DisplayName.Trim();

        /// <summary>The role id with the built-in default applied.</summary>
        public string EffectiveRoleId => string.IsNullOrWhiteSpace(RoleId) ? BuiltInAgentRoleIds.AiNpc : RoleId.Trim();

        /// <summary>The display name followed by every non-empty alias, without duplicates (ordinal, ignoring case).</summary>
        public IReadOnlyList<string> NamesToAnswer()
        {
            List<string> names = new(1 + _aliases.Count);
            AddName(names, EffectiveName);
            foreach (string alias in _aliases)
            {
                AddName(names, alias);
            }

            return names;
        }

        /// <summary>A copy that a later edit of this instance does not change.</summary>
        public NpcPersona Clone()
        {
            return new NpcPersona
            {
                Id = Id,
                DisplayName = DisplayName,
                Aliases = new List<string>(_aliases),
                PromptFragment = PromptFragment,
                VoiceRules = VoiceRules,
                FallbackLines = new List<string>(_fallbackLines),
                RoleId = RoleId,
                RoutingProfileId = RoutingProfileId
            };
        }

        private static void AddName(List<string> names, string name)
        {
            string trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0)
            {
                return;
            }

            foreach (string existing in names)
            {
                if (string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            names.Add(trimmed);
        }
    }
}
