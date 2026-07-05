namespace CoreAI.Ai
{
    /// <summary>
    /// Controls how observed tool results are persisted into durable chat history.
    /// </summary>
    public enum ToolResultMemoryPolicy
    {
        // CompactSummary is zero so default RoleMemoryConfig structs use the roadmap default.

        /// <summary>Do not persist tool results into chat history.</summary>
        None = 1,

        /// <summary>Persist only failed tool results.</summary>
        ErrorsOnly = 2,

        /// <summary>Persist one compact status line per tool result.</summary>
        CompactSummary = 0,

        /// <summary>Persist each tool result with a larger head/tail-truncated detail payload.</summary>
        Full = 3
    }
}