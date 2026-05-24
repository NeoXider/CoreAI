namespace CoreAI.Chat
{
    /// <summary>
    /// Options that control externally submitted chat messages.
    /// </summary>
    public sealed class CoreAiChatExternalSubmitOptions
    {
        /// <summary>
        /// Append user message to chat.
        /// </summary>
        public bool AppendUserMessageToChat { get; set; } = true;

        /// <summary>
        /// Simulated assistant reply.
        /// </summary>
        public string SimulatedAssistantReply { get; set; }
    }
}
