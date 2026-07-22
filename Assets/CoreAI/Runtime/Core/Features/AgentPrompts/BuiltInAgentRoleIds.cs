using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Defines built-in agent role identifiers used by CoreAI.
    /// </summary>
    public static class BuiltInAgentRoleIds
    {
        /// <summary>Creator.</summary>
        public const string Creator = "Creator";

        /// <summary>3D scene builder: places every object itself with explicit coordinates.</summary>
        public const string Builder = "Builder";

        /// <summary>Analyzer.</summary>
        public const string Analyzer = "Analyzer";

        /// <summary>Programmer.</summary>
        public const string Programmer = "Programmer";

        /// <summary>Ai npc.</summary>
        public const string AiNpc = "AINpc";

        /// <summary>Role id for the core gameplay mechanics agent.</summary>
        public const string CoreMechanic = "CoreMechanicAI";

        /// <summary>Plain chat.</summary>
        public const string PlainChat = "PlainChat";

        /// <summary>Smart chat.</summary>
        public const string SmartChat = "SmartChat";

        /// <summary>Merchant.</summary>
        public const string Merchant = "Merchant";

        /// <summary>
        /// Auxiliary routing-only id for transcript compaction completions (never a playable agent).
        /// Host routing may steer this toward a lighter model profile.
        /// </summary>
        public const string ContextCompactionAux = "__CoreAI_ContextCompaction";

        /// <summary>All built in roles.</summary>
        public static readonly IReadOnlyList<string> AllBuiltInRoles = new[]
        {
            Creator,
            Builder,
            Analyzer,
            Programmer,
            AiNpc,
            CoreMechanic,
            PlainChat,
            SmartChat,
            Merchant
        };

        /// <summary>
        /// Returns <c>true</c> when <paramref name="roleId"/> matches one of the built-in roles
        /// listed in <see cref="AllBuiltInRoles"/>. Built-in roles always have a manifest/Resources
        /// fallback prompt, so callers (e.g. <c>AgentBuilder</c> validation) can skip
        /// "missing system prompt" warnings for them.
        /// </summary>
        public static bool IsBuiltIn(string roleId)
        {
            if (string.IsNullOrEmpty(roleId))
            {
                return false;
            }

            for (int i = 0; i < AllBuiltInRoles.Count; i++)
            {
                if (string.Equals(AllBuiltInRoles[i], roleId, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
