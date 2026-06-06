namespace CoreAI.Ai
{
    /// <summary>Single revision entry in a Lua script version record.</summary>
    public sealed class LuaScriptRevision
    {
        public LuaScriptRevision(int index, string source, long utcTicks)
        {
            Index = index;
            Source = source ?? "";
            UtcTicks = utcTicks;
        }

        public int Index { get; }
        public string Source { get; }
        public long UtcTicks { get; }
    }
}