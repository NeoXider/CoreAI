using UnityEngine.UIElements;

namespace CoreAI.ChatMarkdown
{
    /// <summary>
    /// Renders the content of assistant bubbles in <see cref="CoreAI.Chat.CoreAiChatPanel"/>
    /// (<see cref="CoreAI.Chat.CoreAiChatPanel.BubbleRenderer"/>). The panel keeps owning rows, avatars, the
    /// streaming label, scrolling and history; a renderer only decides what a bubble shows. User bubbles and
    /// tool-call lines never reach it.
    /// </summary>
    /// <remarks>
    /// Calls arrive on the main thread and must not throw: they run inside the panel's streaming loop and its
    /// teardown. <see cref="CoreAiChatMarkdownBubbles"/> is the Markdown implementation.
    /// </remarks>
    public interface ICoreAiChatBubbleRenderer
    {
        /// <summary>
        /// Content of a finished assistant bubble: a buffered answer, restored history, a notice or an error. Null
        /// keeps the panel's plain label.
        /// </summary>
        VisualElement CreateAssistantContent(string text);

        /// <summary>
        /// The streaming bubble <paramref name="bubble"/> got more text. Called once per visible chunk; throttling
        /// is the renderer's job.
        /// </summary>
        void OnStreamingTextChanged(Label bubble);

        /// <summary>
        /// <paramref name="bubble"/> stopped streaming and its text is final: the turn ended, a tool round sealed it,
        /// the next message of the stream opened a new bubble, or the panel let the tree go. Called exactly once per
        /// bubble, with that bubble's own text only.
        /// </summary>
        void OnStreamingBubbleCompleted(Label bubble);
    }
}
