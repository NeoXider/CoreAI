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

        /// <summary>Удалить только историю чата (ChatHistory) роли.</summary>
        void ClearChatHistory(string roleId);

        /// <summary>
        /// Добавить сообщение в историю чата (Тип 2: ChatHistory).
        /// Используется LLMAgent для сохранения полного контекста диалога.
        /// </summary>
        /// <param name="roleId">ID роли агента.</param>
        /// <param name="role">"user" или "assistant".</param>
        /// <param name="content">Текст сообщения.</param>
        /// <param name="persistToDisk">Сохранять ли на диск (если хранилище поддерживает).</param>
        void AppendChatMessage(string roleId, string role, string content, bool persistToDisk = true);

        /// <summary>
        /// Получить историю чата (Тип 2: ChatHistory).
        /// </summary>
        /// <param name="roleId">ID роли агента.</param>
        /// <param name="maxMessages">Максимум последних сообщений (0 = все).</param>
        /// <returns>Список сообщений или null если истории нет.</returns>
        ChatMessage[] GetChatHistory(string roleId, int maxMessages = 0);
    }

    /// <summary>Одно сообщение в истории чата.</summary>
    [System.Serializable]
    public struct ChatMessage
    {
        public string Role; // "user" | "assistant" | "system"
        public string Content;
        public long Timestamp; // Unix timestamp

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
            Timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}