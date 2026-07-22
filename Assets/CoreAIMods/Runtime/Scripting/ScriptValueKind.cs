namespace CoreAI.Scripting
{
    /// <summary>
    /// Engine-neutral classification of a script value. Mirrors the value categories every candidate
    /// Lua-family VM exposes, so hosts can branch on shape without referencing VM types.
    /// </summary>
    public enum ScriptValueKind
    {
        Nil,
        Boolean,
        Number,
        String,
        Table,
        Function,
        Other
    }
}
