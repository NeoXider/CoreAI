#if COREAI_LLM
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
        /// <paramref name="total"/> starts a fresh one. The returned instance is a dedicated
        /// accumulator object - the provider's own <see cref="MEAI.UsageDetails"/> is never mutated.
        /// </summary>
        /// <remarks>
        /// The addition itself is the native <c>UsageDetails.Add</c>: it sums every typed counter the
        /// running MEAI version has and merges <c>AdditionalCounts</c> key by key, adding the values of
        /// keys present on both sides. That merge is what carries prompt-cache reads and writes and
        /// every other vendor counter through a multi-roundtrip tool turn — CoreAI keeps them there
        /// rather than in the typed 10.x-only properties, because it must compile against the
        /// consumer's 9.10.2 floor. The only thing left for this wrapper is the copy: <c>Add</c> mutates
        /// its receiver, and the provider's own <see cref="MEAI.UsageDetails"/> must not be touched.
        /// </remarks>
        public static MEAI.UsageDetails Accumulate(MEAI.UsageDetails total, MEAI.UsageDetails add)
        {
            if (add == null)
            {
                return total;
            }

            total ??= new MEAI.UsageDetails();
            total.Add(add);
            return total;
        }
    }
}
#endif
