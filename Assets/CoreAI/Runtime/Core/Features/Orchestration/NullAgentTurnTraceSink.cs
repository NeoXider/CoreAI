namespace CoreAI.Ai
{
    /// <summary>
    /// No-op trace sink.
    /// </summary>
    public sealed class NullAgentTurnTraceSink : IAgentTurnTraceSink
    {
        /// <inheritdoc />
        public void Record(AgentTurnTrace trace)
        {
        }
    }
}
