namespace CoreAI.Ai
{
    /// <summary>
    /// Trace sink used when the host does not persist per-turn agent traces.
    /// </summary>
    public sealed class NullAgentTurnTraceSink : IAgentTurnTraceSink
    {
        /// <inheritdoc />
        public void Record(AgentTurnTrace trace)
        {
        }
    }
}