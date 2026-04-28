using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Receives portable LLM usage records for quota, analytics, and cost accounting.
    /// </summary>
    public interface ILlmUsageSink
    {
        /// <summary>Records usage for one LLM request.</summary>
        Task RecordAsync(LlmUsageRecord record, CancellationToken cancellationToken = default);
    }
}
