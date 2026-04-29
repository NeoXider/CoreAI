namespace CoreAI.Chat
{
    /// <summary>
    /// Where a chat request originated; useful for future configurators to scope
    /// behaviour (e.g. only UI vs external submit).
    /// </summary>
    public enum ChatRequestSource
    {
        /// <summary>User typed into the chat panel and pressed Send.</summary>
        UiInput = 0,

        /// <summary><see cref="CoreAiChatPanel.SubmitMessageFromExternalAsync"/> from game/test code.</summary>
        ExternalSubmit = 1,

        /// <summary>Programmatic <see cref="CoreAiChatService"/> call without the panel.</summary>
        Programmatic = 2
    }

    /// <summary>
    /// Read-only snapshot for future chat request hooks (preview). Carries enough info
    /// to decide whether to mutate an <see cref="CoreAI.Ai.AiTaskRequest"/> and how — e.g. for
    /// <see cref="IChatRequestConfigurator"/> when a registration API exists.
    /// </summary>
    public sealed class ChatRequestContext
    {
        public ChatRequestContext(string roleId, string userText, ChatRequestSource source, bool isStreaming)
        {
            RoleId = roleId ?? string.Empty;
            UserText = userText ?? string.Empty;
            Source = source;
            IsStreaming = isStreaming;
        }

        /// <summary>Agent role the request will be routed to (e.g. <c>"Teacher"</c>).</summary>
        public string RoleId { get; }

        /// <summary>Raw user text the chat is about to send (already trimmed/clipped).</summary>
        public string UserText { get; }

        /// <summary>Channel that produced the request.</summary>
        public ChatRequestSource Source { get; }

        /// <summary>
        /// Whether the dispatch will use streaming. Configurators rarely need this,
        /// but useful for diagnostics or for streaming-only behaviours.
        /// </summary>
        public bool IsStreaming { get; }
    }
}
