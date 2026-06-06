namespace CoreAI.Ai
{
    /// <summary>IAiOrchestrationMetrics interface.</summary>
    public interface IAiOrchestrationMetrics
    {
        /// <summary>Records the outcome and duration of an LLM completion request.</summary>
        void RecordLlmCompletion(string roleId, string traceId, bool ok, double wallMs);

        /// <summary>Records a structured-response validation retry and its reason.</summary>
        void RecordStructuredRetry(string roleId, string traceId, string reason);

        /// <summary>Records that an AI command was published for the given role and trace.</summary>
        void RecordCommandPublished(string roleId, string traceId);
    }
}