using CoreAI.Messaging;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Portable abstraction for publishing tool-call lifecycle events
    /// (<see cref="LlmToolCallStarted"/>, <see cref="LlmToolCallCompleted"/>,
    /// <see cref="LlmToolCallFailed"/>).
    ///
    /// <para>Unity hosts provide a MessagePipe-backed implementation;
    /// non-Unity hosts can supply their own or use <see cref="NullToolCallEventPublisher"/>.</para>
    /// </summary>
    public interface IToolCallEventPublisher
    {
        /// <summary>Publish that a tool call has started.</summary>
        void PublishStarted(LlmToolCallInfo info);

        /// <summary>Publish that a tool call completed successfully.</summary>
        void PublishCompleted(LlmToolCallInfo info, string resultJson, double durationMs);

        /// <summary>Publish that a tool call failed.</summary>
        void PublishFailed(LlmToolCallInfo info, string error, double durationMs);
    }

    /// <summary>
    /// No-op implementation for environments without an event bus.
    /// </summary>
    public sealed class NullToolCallEventPublisher : IToolCallEventPublisher
    {
        public static readonly NullToolCallEventPublisher Instance = new();

        public void PublishStarted(LlmToolCallInfo info) { }
        public void PublishCompleted(LlmToolCallInfo info, string resultJson, double durationMs) { }
        public void PublishFailed(LlmToolCallInfo info, string error, double durationMs) { }
    }
}
