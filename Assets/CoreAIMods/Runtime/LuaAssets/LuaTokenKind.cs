namespace CoreAI.LuaAssets
{
    /// <summary>
    /// Coarse lexical categories produced by <see cref="LuaTokenizer"/> for Lua/Luau source. Consumers
    /// (editor syntax highlighting today, a future in-game console) map each kind to a display color.
    /// </summary>
    public enum LuaTokenKind
    {
        Whitespace,
        Comment,
        Keyword,
        Identifier,
        FunctionCall,
        Global,
        String,
        LongString,
        InterpolatedString,
        Number,
        TypeAnnotation,
        Operator
    }
}
