using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>Version record for a mutable data overlay payload.</summary>
    public sealed class DataOverlayVersionRecord
    {
        public DataOverlayVersionRecord(
            string overlayKey,
            string originalPayload,
            string currentPayload,
            IReadOnlyList<LuaScriptRevision> history)
        {
            OverlayKey = overlayKey ?? "";
            OriginalPayload = originalPayload ?? "";
            CurrentPayload = currentPayload ?? "";
            History = history ?? new LuaScriptRevision[0];
        }

        public string OverlayKey { get; }
        public string OriginalPayload { get; }
        public string CurrentPayload { get; }
        public IReadOnlyList<LuaScriptRevision> History { get; }
    }
}