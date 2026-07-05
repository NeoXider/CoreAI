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
        Clear = 2,

        /// <summary>Replaces exact text in the memory document.</summary>
        StrReplace = 3,

        /// <summary>Inserts text into the memory document at a line or anchor.</summary>
        Insert = 4,

        /// <summary>Deletes exact text from the memory document.</summary>
        Delete = 5,

        /// <summary>Renames a leading section/key label in the memory document.</summary>
        Rename = 6
    }
}