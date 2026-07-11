using System.Collections.Concurrent;

namespace CoreAI.Audit
{
    public static class AuditContext
    {
        private static readonly ConcurrentDictionary<string, string> PromptHashes = new();
        private static readonly ConcurrentDictionary<string, string> Models = new();

        public static void SetPromptHash(string traceId, string promptHash)
        {
            if (!string.IsNullOrEmpty(traceId))
            {
                PromptHashes[traceId] = promptHash ?? "";
            }
        }

        public static string GetPromptHash(string traceId)
        {
            return !string.IsNullOrEmpty(traceId) && PromptHashes.TryGetValue(traceId, out string hash) ? hash : "";
        }

        public static void SetModel(string traceId, string model)
        {
            if (!string.IsNullOrEmpty(traceId))
            {
                Models[traceId] = model ?? "";
            }
        }

        public static string GetModel(string traceId)
        {
            return !string.IsNullOrEmpty(traceId) && Models.TryGetValue(traceId, out string model) ? model : "";
        }

        public static void Cleanup(string traceId)
        {
            if (!string.IsNullOrEmpty(traceId))
            {
                PromptHashes.TryRemove(traceId, out _);
                Models.TryRemove(traceId, out _);
            }
        }
    }
}