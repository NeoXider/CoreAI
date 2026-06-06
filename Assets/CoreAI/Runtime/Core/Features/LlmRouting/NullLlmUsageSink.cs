using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Usage sink used when a host does not record token or request usage.
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