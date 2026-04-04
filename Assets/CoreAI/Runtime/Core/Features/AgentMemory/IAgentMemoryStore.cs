namespace CoreAI.Ai
{
    /// <summary>Хранилище долговременной «памяти» агента по id роли.</summary>
    public interface IAgentMemoryStore
    {
        /// <summary>Прочитать состояние; <c>false</c>, если записи нет.</summary>
        bool TryLoad(string roleId, out AgentMemoryState state);

        /// <summary>Сохранить или перезаписать память роли.</summary>
        void Save(string roleId, AgentMemoryState state);

        /// <summary>Удалить память роли.</summary>
        void Clear(string roleId);
    }
}