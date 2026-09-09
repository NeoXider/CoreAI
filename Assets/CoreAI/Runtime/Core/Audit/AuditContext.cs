using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CoreAI.Audit
{
    /// <summary>
    /// Per-trace facts the audit log stamps onto its entries: the prompt fingerprint and the model.
    /// <para>
    /// Retention is bounded. Every orchestrated request registers its prompt hash here, but the only
    /// caller of <see cref="Cleanup"/> is the Unity audit interceptor - a headless or portable host that
    /// never installs it accumulated one entry per request for the life of the process. Traces are
    /// consulted while their request is in flight and shortly after it completes, so once
    /// <see cref="MaxTrackedTraces"/> traces are held the oldest registered one is dropped. Concurrency in
    /// the orchestrator is at most a handful of requests, so no live trace is ever evicted.
    /// </para>
    /// </summary>
    public static class AuditContext
    {
        /// <summary>Upper bound on traces retained without an explicit <see cref="Cleanup"/>.</summary>
        public const int MaxTrackedTraces = 256;

        private static readonly ConcurrentDictionary<string, string> PromptHashes = new();
        private static readonly ConcurrentDictionary<string, string> Models = new();
        private static readonly ConcurrentQueue<string> RegistrationOrder = new();

        public static void SetPromptHash(string traceId, string promptHash)
        {
            if (!string.IsNullOrEmpty(traceId))
            {
                bool isNew = !PromptHashes.ContainsKey(traceId) && !Models.ContainsKey(traceId);
                PromptHashes[traceId] = promptHash ?? "";
                if (isNew)
                {
                    Track(traceId);
                }
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
                bool isNew = !PromptHashes.ContainsKey(traceId) && !Models.ContainsKey(traceId);
                Models[traceId] = model ?? "";
                if (isNew)
                {
                    Track(traceId);
                }
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

        /// <summary>Number of traces currently holding a prompt hash or a model (diagnostics / tests).</summary>
        public static int TrackedTraceCount
        {
            get
            {
                int count = PromptHashes.Count;
                foreach (KeyValuePair<string, string> model in Models)
                {
                    if (!PromptHashes.ContainsKey(model.Key))
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        private static void Track(string traceId)
        {
            RegistrationOrder.Enqueue(traceId);
            while (RegistrationOrder.Count > MaxTrackedTraces && RegistrationOrder.TryDequeue(out string oldest))
            {
                // A trace already released by Cleanup is a no-op here; the queue entry was its only remnant.
                PromptHashes.TryRemove(oldest, out _);
                Models.TryRemove(oldest, out _);
            }
        }
    }
}
