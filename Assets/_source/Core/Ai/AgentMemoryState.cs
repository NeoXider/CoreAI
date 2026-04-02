namespace CoreAI.Ai
{
    /// <summary>
    /// Память агента (роль-специфичная) и вспомогательное состояние.
    /// </summary>
    public sealed class AgentMemoryState
    {
        /// <summary>
        /// Последний системный промпт, реально отправленный в LLM (для отладки/воспроизводимости).
        /// </summary>
        public string LastSystemPrompt { get; set; }

        /// <summary>
        /// Произвольная “память”, которую агент сам решает сохранять.
        /// </summary>
        public string Memory { get; set; }
    }
}

