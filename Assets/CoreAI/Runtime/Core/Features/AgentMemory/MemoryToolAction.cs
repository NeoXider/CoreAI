namespace CoreAI.Ai
{
    /// <summary>
    /// Defines operations supported by the memory tool.
    /// </summary>
    public enum MemoryToolAction
    {
        /// <summary>Replaces an existing memory value with the supplied content.</summary>
        Write = 0,

        /// <summary>Appends supplied content to the existing memory value.</summary>
        Append = 1,

        /// <summary>Clears all memory state for the requested role.</summary>
        Clear = 2
    }
}
