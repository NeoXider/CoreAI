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

        /// <summary>
        /// The host's own deadline for this turn, kept apart from the caller's cancellation token. When it
        /// fires while the caller's token is still alive, the turn is stopped and reported as a
        /// <see cref="CoreAI.Ai.LlmErrorCode.Timeout"/> (the panel shows <c>ResolveTimeoutMessage</c>);
        /// when the caller's token fires, the turn is a cancellation.
        /// <para>
        /// WHY a separate token: a deadline armed on the caller's token itself (<c>CancelAfter</c> on the
        /// token passed to <c>SubmitMessageFromExternalAsync</c>) cannot be told apart from a stop, so since
        /// 7.44.0 it is reported as a cancellation. Pass the deadline here instead.
        /// </para>
        /// </summary>
        public System.Threading.CancellationToken DeadlineCancellationToken { get; set; }
    }
}
