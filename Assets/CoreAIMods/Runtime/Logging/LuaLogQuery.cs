namespace CoreAI.Ai.Logging
{
    /// <summary>
    /// Filter/paging parameters for <see cref="ILuaLogService.Query"/>. All filters are optional and
    /// combine with AND semantics; results are returned oldest-first ("newest-last"), capped to the
    /// newest <see cref="MaxCount"/> entries that matched.
    /// </summary>
    public sealed class LuaLogQuery
    {
        /// <summary>When set, only entries from this exact mod id are returned.</summary>
        public string ModId;

        /// <summary>When set, only entries whose <see cref="LuaLogEntry.Level"/> is at or above this severity.</summary>
        public LuaLogLevel? MinLevel;

        /// <summary>Only entries with <see cref="LuaLogEntry.Sequence"/> strictly greater than this are returned.</summary>
        public long SinceSequence;

        /// <summary>When set, only entries whose <see cref="LuaLogEntry.Message"/> contains this text (ordinal, case-insensitive).</summary>
        public string TextContains;

        /// <summary>Maximum number of entries to return. Non-positive values are treated as "unbounded".</summary>
        public int MaxCount = int.MaxValue;
    }
}
