#if !COREAI_NO_LLM
using System.Collections.Generic;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Shared per-roundtrip usage accumulation for multi-roundtrip tool-calling turns.
    /// Providers report usage once per model roundtrip; both the streaming loop
    /// (<c>MeaiLlmClient</c>) and the non-streaming loop (<see cref="SmartToolCallingChatClient"/>)
    /// sum those reports so a whole turn reports the total tokens it actually burned instead of
    /// only the last roundtrip's.
    /// </summary>
    public static class LlmUsageAccumulator
    {
        /// <summary>
        /// Adds <paramref name="add"/> into <paramref name="total"/> and returns the running total.
        /// A <c>null</c> <paramref name="add"/> leaves the total untouched; a <c>null</c>
        /// <paramref name="total"/> starts a fresh one. Additional provider counts (e.g. cache
        /// read/write tokens) are summed key-by-key. The returned instance is a dedicated
        /// accumulator object - the provider's own <see cref="MEAI.UsageDetails"/> is never mutated.
        /// </summary>
        public static MEAI.UsageDetails Accumulate(MEAI.UsageDetails total, MEAI.UsageDetails add)
        {
            if (add == null)
            {
                return total;
            }

            if (total == null)
            {
                total = new MEAI.UsageDetails();
            }

            if (add.InputTokenCount.HasValue)
            {
                total.InputTokenCount = (total.InputTokenCount ?? 0) + add.InputTokenCount.Value;
            }

            if (add.OutputTokenCount.HasValue)
            {
                total.OutputTokenCount = (total.OutputTokenCount ?? 0) + add.OutputTokenCount.Value;
            }

            if (add.TotalTokenCount.HasValue)
            {
                total.TotalTokenCount = (total.TotalTokenCount ?? 0) + add.TotalTokenCount.Value;
            }

            if (add.AdditionalCounts != null)
            {
                total.AdditionalCounts ??= new MEAI.AdditionalPropertiesDictionary<long>();
                foreach (KeyValuePair<string, long> kv in add.AdditionalCounts)
                {
                    total.AdditionalCounts[kv.Key] = total.AdditionalCounts.TryGetValue(kv.Key, out long existing)
                        ? existing + kv.Value
                        : kv.Value;
                }
            }

            return total;
        }
    }
}
#endif
