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
}
