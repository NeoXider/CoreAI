namespace CoreAI.Ai
{
    /// <summary>IAiOrchestrationMetrics interface.</summary>
    public interface IAiOrchestrationMetrics
    {
        /// <summary>Records the actor, role, outcome, and duration of an LLM completion request.</summary>
        void RecordLlmCompletion(string actorId, string roleId, string traceId, bool ok, double wallMs);

        /// <summary>Records an actor's structured-response validation retry and its reason.</summary>
        void RecordStructuredRetry(string actorId, string roleId, string traceId, string reason);

        /// <summary>Records that an AI command was published for the given actor, role, and trace.</summary>
        void RecordCommandPublished(string actorId, string roleId, string traceId);
    }
}
