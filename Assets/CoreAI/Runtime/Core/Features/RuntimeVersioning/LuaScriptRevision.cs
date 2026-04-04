namespace CoreAI.Ai
{
    /// <summary>Один зафиксированный успешный снимок Lua для слота <see cref="ILuaScriptVersionStore"/>.</summary>
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