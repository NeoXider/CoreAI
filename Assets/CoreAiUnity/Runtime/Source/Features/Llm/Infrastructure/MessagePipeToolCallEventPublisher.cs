using System;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using MessagePipe;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Unity-side implementation of <see cref="IToolCallEventPublisher"/>
    /// that delegates to <see cref="GlobalMessagePipe"/> for tool-call lifecycle events.
    /// </summary>
    public sealed class MessagePipeToolCallEventPublisher : IToolCallEventPublisher
    {
        public static readonly MessagePipeToolCallEventPublisher Instance = new();

        public void PublishStarted(LlmToolCallInfo info)
        {
            LlmToolCallStarted evt = new(info);
            CoreAi.NotifyToolCallStarted(evt);

            if (!GlobalMessagePipe.IsInitialized)
            {
                return;
            }

            try
            {
                GlobalMessagePipe.GetPublisher<LlmToolCallStarted>()
                    .Publish(evt);
            }
            catch (Exception ex)
            {
                GameLoggerUnscopedFallback.Instance.LogWarning(GameLogFeature.MessagePipe,
                    $"[MessagePipeToolCallEventPublisher] PublishStarted failed: {ex}");
            }
        }

        public void PublishCompleted(LlmToolCallInfo info, string resultJson, double durationMs)
        {
            LlmToolCallCompleted evt = new(info, resultJson, durationMs);
            CoreAi.NotifyToolCallCompleted(evt);

            if (!GlobalMessagePipe.IsInitialized)
            {
                return;
            }

            try
            {
                GlobalMessagePipe.GetPublisher<LlmToolCallCompleted>()
                    .Publish(evt);
            }
            catch (Exception ex)
            {
                GameLoggerUnscopedFallback.Instance.LogWarning(GameLogFeature.MessagePipe,
                    $"[MessagePipeToolCallEventPublisher] PublishCompleted failed: {ex}");
            }
        }

        public void PublishFailed(LlmToolCallInfo info, string error, double durationMs)
        {
            LlmToolCallFailed evt = new(info, error, durationMs);
            CoreAi.NotifyToolCallFailed(evt);

            if (!GlobalMessagePipe.IsInitialized)
            {
                return;
            }

            try
            {
                GlobalMessagePipe.GetPublisher<LlmToolCallFailed>()
                    .Publish(evt);
            }
            catch (Exception ex)
            {
                GameLoggerUnscopedFallback.Instance.LogWarning(GameLogFeature.MessagePipe,
                    $"[MessagePipeToolCallEventPublisher] PublishFailed failed: {ex}");
            }
        }
    }
}