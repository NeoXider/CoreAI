namespace CoreAI.Ai
{
    public sealed class NullAgentMemoryStore : IAgentMemoryStore
    {
        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            state = null;
            return false;
        }

        public void Save(string roleId, AgentMemoryState state)
        {
        }

        public void Clear(string roleId)
        {
        }
    }
}

