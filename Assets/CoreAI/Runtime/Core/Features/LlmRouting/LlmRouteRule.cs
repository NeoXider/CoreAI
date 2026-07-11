namespace CoreAI.Ai
{
    /// <summary>
    /// Portable role-to-profile routing rule.
    /// </summary>
    public sealed class LlmRouteRule
    {
        /// <summary>Exact role id, prefix pattern ending with <c>*</c>, or <c>*</c> wildcard.</summary>
        public string RolePattern { get; set; } = "*";

        /// <summary>Target profile id.</summary>
        public string ProfileId { get; set; } = "default";

        /// <summary>Lower values are evaluated earlier.</summary>
        public int SortOrder { get; set; }
    }
}
