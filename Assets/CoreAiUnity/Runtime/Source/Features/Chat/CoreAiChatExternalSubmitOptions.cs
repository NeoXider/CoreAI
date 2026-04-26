namespace CoreAI.Chat
{
    /// <summary>
    /// Параметры программной отправки <see cref="CoreAiChatPanel.SubmitMessageFromExternalAsync"/>.
    /// </summary>
    public sealed class CoreAiChatExternalSubmitOptions
    {
        /// <summary>
        /// Показать текст запроса пузырём пользователя в ленте (по умолчанию <c>true</c>).
        /// </summary>
        public bool AppendUserMessageToChat { get; set; } = true;

        /// <summary>
        /// Если не null и не пустая строка — LLM не вызывается; текст показывается как ответ ассистента
        /// (после strip think-блоков и <see cref="CoreAiChatPanel.FormatResponseText"/>).
        /// </summary>
        public string SimulatedAssistantReply { get; set; }
    }
}
