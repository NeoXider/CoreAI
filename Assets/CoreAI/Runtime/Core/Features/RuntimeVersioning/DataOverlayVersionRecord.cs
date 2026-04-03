using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>Версионирование JSON/текстовых оверлеев конфигурации (ключ — логический use case, например <c>progression.baseline</c>).</summary>
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
