namespace CoreAI.Ai
{
    /// <summary>
    /// Result of resolving an agent role to an LLM route profile.
    /// </summary>
    public readonly struct LlmRouteResolution
    {
        /// <summary>Creates a route resolution result.</summary>
        public LlmRouteResolution(string roleId, LlmRouteProfile profile, LlmRouteRule rule)
        {
            RoleId = roleId ?? "";
            Profile = profile;
            Rule = rule;
        }

        /// <summary>Resolved role id.</summary>
        public string RoleId { get; }

        /// <summary>Matched profile, or null when no rule/profile matched.</summary>
        public LlmRouteProfile Profile { get; }

        /// <summary>Matched rule, or null for fallback.</summary>
        public LlmRouteRule Rule { get; }

        /// <summary>True when a profile was matched.</summary>
        public bool Found => Profile != null;
    }
}
