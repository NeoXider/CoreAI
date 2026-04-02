namespace CoreAI.Ai
{
    /// <summary>Хранилище-заглушка: ничего не сохраняет, <see cref="TryLoad"/> всегда <c>false</c>.</summary>
    public sealed class NullAgentMemoryStore : IAgentMemoryStore
    {
        /// <inheritdoc />
        public bool TryLoad(string roleId, out AgentMemoryState state)
        {
            state = null;
            return false;
        }

        public void Save(string roleId, AgentMemoryState state)
        {
        }

        /// <inheritdoc />
        public void Clear(string roleId)
        {
        }
    }
}

