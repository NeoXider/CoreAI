namespace CoreAI.Ai
{
    /// <summary>Metrics collector used when the host does not record orchestration metrics.</summary>
    public sealed class NullAiOrchestrationMetrics : IAiOrchestrationMetrics
    {
        /// <inheritdoc />
        public void RecordLlmCompletion(string roleId, string traceId, bool ok, double wallMs)
        {
        }

        /// <inheritdoc />
        public void RecordStructuredRetry(string roleId, string traceId, string reason)
        {
        }

        /// <inheritdoc />
        public void RecordCommandPublished(string roleId, string traceId)
        {
        }
    }
}
