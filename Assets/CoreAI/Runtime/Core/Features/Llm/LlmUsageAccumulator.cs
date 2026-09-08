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
        /// Само сложение делает штатный <c>UsageDetails.Add</c>: он суммирует ВСЕ типизированные
        /// поля (включая <c>CachedInputTokenCount</c> и <c>ReasoningTokenCount</c>) и сливает
        /// <c>AdditionalCounts</c> ключ-к-ключу. Прежняя ручная версия складывала только
        /// input/output/total и словарь, из-за чего чтение промпт-кэша, пришедшее штатным полем,
        /// терялось на каждом многораундовом ходе с инструментами.
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
