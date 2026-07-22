namespace CoreAI.LuaAssets
{
    /// <summary>
    /// One lexical span in a tokenized Lua/Luau source string. Stores offsets into the original source
    /// rather than a copied substring so tokenizing a large file stays cheap; call <see cref="GetText"/>
    /// against the same source string used to tokenize.
    /// </summary>
    public readonly struct LuaToken
    {
        public readonly LuaTokenKind Kind;
        public readonly int Start;
        public readonly int Length;

        public LuaToken(LuaTokenKind kind, int start, int length)
        {
            Kind = kind;
            Start = start;
            Length = length;
        }

        public string GetText(string source) => source.Substring(Start, Length);
    }
}
