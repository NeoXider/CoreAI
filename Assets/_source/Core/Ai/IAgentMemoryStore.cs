namespace CoreAI.Ai
{
    public interface IAgentMemoryStore
    {
        bool TryLoad(string roleId, out AgentMemoryState state);
        void Save(string roleId, AgentMemoryState state);
        void Clear(string roleId);
    }
}

