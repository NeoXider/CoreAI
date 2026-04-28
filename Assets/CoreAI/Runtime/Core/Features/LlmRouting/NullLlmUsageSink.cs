using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// No-op usage sink used when a host does not record usage.
    /// </summary>
    public sealed class NullLlmUsageSink : ILlmUsageSink
    {
        /// <inheritdoc />
        public Task RecordAsync(LlmUsageRecord record, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
