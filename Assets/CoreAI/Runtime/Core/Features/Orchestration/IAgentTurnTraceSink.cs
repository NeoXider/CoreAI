namespace CoreAI.Ai
{
    /// <summary>
    /// Receives diagnostic traces for agent turns.
    /// </summary>
    public interface IAgentTurnTraceSink
    {
        /// <summary>Records a trace.</summary>
        void Record(AgentTurnTrace trace);
    }

    /// <summary>
    /// Read-only access to the most recent turn trace per role.
    /// Implemented by sinks that retain traces (e.g. <see cref="InMemoryAgentTurnTraceSink"/>);
    /// the default null sink does not implement it, so live diagnostics degrade gracefully.
    /// </summary>
    public interface IAgentTurnTraceReader
    {
        /// <summary>
        /// Returns the most recent trace recorded for <paramref name="roleId"/>, if any.
        /// The returned trace must not be mutated; this call never persists anything.
        /// </summary>
        bool TryGetLatestTrace(string roleId, out AgentTurnTrace trace);
    }
}
