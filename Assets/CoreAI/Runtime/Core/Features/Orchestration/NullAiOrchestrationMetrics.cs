namespace CoreAI.Ai
{
    /// <summary>Metrics collector used when the host does not record orchestration metrics.</summary>
    public sealed class NullAiOrchestrationMetrics : IAiOrchestrationMetrics
    {
        /// <inheritdoc />
        public void RecordLlmCompletion(
            string actorId,
            string roleId,
            string traceId,
            AiLlmCompletionOutcome outcome,
            double wallMs)
        {
        }

        /// <inheritdoc />
        public void RecordStructuredRetry(string actorId, string roleId, string traceId, string reason)
        {
        }

        /// <inheritdoc />
        public void RecordCommandPublished(string actorId, string roleId, string traceId)
        {
        }
    }
}
