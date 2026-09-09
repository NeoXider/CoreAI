using CoreAI.Ai;

namespace CoreAI.Chat
{
    /// <summary>Why an external message never entered an owned panel turn.</summary>
    public enum CoreAiChatExternalSubmitRejection
    {
        None,
        Busy,
        Inactive,
        EmptyInput,
        Filtered,
        Cancelled,
        ServiceUnavailable,
        TypedResultsUnavailable
    }

    /// <summary>
    /// Panel admission and the existing task completion, captured for one invocation only.
    /// Admission is not success: an admitted failure may already have executed tools and must not be replayed automatically.
    /// </summary>
    public sealed class CoreAiChatExternalSubmitResult
    {
        /// <summary>True once this invocation owns a panel turn.</summary>
        public bool Admitted { get; internal set; }
        /// <summary>The generation owned by this invocation; zero when no turn was admitted.</summary>
        public int TurnGeneration { get; internal set; }
        /// <summary>Pre-admission rejection, or None for an admitted turn.</summary>
        public CoreAiChatExternalSubmitRejection Rejection { get; internal set; }
        /// <summary>Typed terminal status, partial content, tools and provider metadata.</summary>
        public LlmCompletionResult Completion { get; internal set; }
    }
}
