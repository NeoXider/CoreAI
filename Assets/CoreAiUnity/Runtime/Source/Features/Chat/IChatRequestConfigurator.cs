using CoreAI.Ai;

namespace CoreAI.Chat
{
    /// <summary>
    /// Preview contract for a plug-in that mutates chat-driven <see cref="AiTaskRequest"/>s
    /// (tool allowlists, forced tool mode, max tokens, etc.).
    /// <para>
    /// <b>Supported host pattern today:</b> derive from <see cref="CoreAiChatPanel"/> and override
    /// <see cref="CoreAiChatPanel.BuildAiTaskRequest(string, string)"/>. That path is used for
    /// player input, <see cref="CoreAiChatPanel.SubmitMessageFromExternalAsync"/>, and any code
    /// that funnels through the panel — one place to keep tool policy consistent with the orchestrator.
    /// </para>
    /// <para>
    /// <see cref="IChatRequestConfigurator"/> is reserved for a future DI registration API; it is
    /// not yet invoked by CoreAI. Do not rely on this interface for runtime behavior until a release
    /// note documents registration and ordering.
    /// </para>
    /// </summary>
    public interface IChatRequestConfigurator
    {
        /// <summary>
        /// Cheap pre-check; return <c>false</c> to skip <see cref="Configure"/>.
        /// Called for every chat request — keep allocations to a minimum.
        /// </summary>
        bool AppliesTo(ChatRequestContext context);

        /// <summary>
        /// Mutate the request before it is dispatched to the orchestrator.
        /// </summary>
        void Configure(AiTaskRequest request, ChatRequestContext context);
    }
}
