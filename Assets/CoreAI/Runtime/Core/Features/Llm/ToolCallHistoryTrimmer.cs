#if COREAI_LLM
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Shared tool-call history trimming used by BOTH tool-calling loops
    /// (<see cref="SmartToolCallingChatClient"/> non-streaming and the streaming loop in
    /// <c>MeaiLlmClient</c>), so history-growth behavior is identical regardless of mode.
    /// <para>
    /// Semantics: system and original user messages are always preserved; only tool-related
    /// messages (an Assistant message carrying <see cref="MEAI.FunctionCallContent"/> plus the
    /// Tool result message(s) answering it) count toward the cap, and the OLDEST resolved
    /// exchanges are dropped first. Trimming always removes whole Assistant+Tool units so a
    /// surviving <c>tool</c>-role message is never orphaned from its <c>tool_calls</c> message
    /// (providers reject orphans with "messages with role 'tool' must be a response to a
    /// preceding message with 'tool_calls'").
    /// </para>
    /// </summary>
    public static class ToolCallHistoryTrimmer
    {
        // WHY: one table, one marker object per kind, so a message is tagged without a wrapper type and the tag dies with the message.
        private static readonly ConditionalWeakTable<MEAI.ChatMessage, object> ImageFeedbackMessages = new();
        private static readonly object CameraFeedbackMarker = new();
        private static readonly object ToolImageFeedbackMarker = new();

        internal const string CameraFeedbackPrompt =
            "Camera frame from your latest capture. Inspect the image and " +
            "continue the requested scene work using what you see.";

        /// <summary>The non-camera prompt when the producing call is no longer in the history.</summary>
        internal const string UnnamedToolImageFeedbackPrompt =
            "Image returned by a tool. Inspect it and continue with the task.";

        /// <summary>How many non-camera tool image messages one history keeps (the newest win).</summary>
        internal const int MaxToolImageFeedbackMessages = 8;

        /// <summary>
        /// The text of the user message that carries a non-camera tool's images, e.g.
        /// <c>Image returned by tool 'render_after'. Inspect it and continue with the task.</c>
        /// </summary>
        internal static string ToolImageFeedbackPrompt(string toolName, int imageCount)
        {
            if (string.IsNullOrWhiteSpace(toolName) && imageCount <= 1)
            {
                return UnnamedToolImageFeedbackPrompt;
            }

            string source = string.IsNullOrWhiteSpace(toolName) ? "a tool" : "tool '" + toolName + "'";
            return imageCount > 1
                ? imageCount + " images returned by " + source + ". Inspect them and continue with the task."
                : "Image returned by " + source + ". Inspect it and continue with the task.";
        }

        internal static void MarkCameraFeedback(MEAI.ChatMessage message)
        {
            ImageFeedbackMessages.Add(message, CameraFeedbackMarker);
        }

        internal static void MarkToolImageFeedback(MEAI.ChatMessage message)
        {
            ImageFeedbackMessages.Add(message, ToolImageFeedbackMarker);
        }

        internal static bool IsCameraFeedback(MEAI.ChatMessage message)
        {
            return ImageFeedbackMessages.TryGetValue(message, out object marker) &&
                   ReferenceEquals(marker, CameraFeedbackMarker);
        }

        internal static bool IsToolImageFeedback(MEAI.ChatMessage message)
        {
            return ImageFeedbackMessages.TryGetValue(message, out object marker) &&
                   ReferenceEquals(marker, ToolImageFeedbackMarker);
        }

        internal static void RemovePriorCameraFeedback(List<MEAI.ChatMessage> messages)
        {
            // WHY: a benchmark can disable tool-history trimming to retain every build command, and camera bytes still need a separate bound, so the newest frame supersedes older rendered frames.
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (IsCameraFeedback(messages[i]))
                {
                    messages.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Drops the oldest non-camera tool image messages beyond <paramref name="keep"/>. Unlike camera frames
        /// they are not superseded by the next image (a model may compare two renders), so this is their bound.
        /// </summary>
        internal static void RemoveExcessToolImageFeedback(List<MEAI.ChatMessage> messages, int keep)
        {
            int seen = 0;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (IsToolImageFeedback(messages[i]) && ++seen > keep)
                {
                    messages.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Removes the oldest tool-call units (an Assistant tool-call turn together with the
        /// Tool result turn(s) that answer it) to keep total tool-related messages within
        /// <paramref name="maxToolMessages"/>. Mutates <paramref name="messages"/> in place and
        /// returns how many messages were removed. A non-positive cap disables trimming
        /// (0 = unlimited, mirroring <see cref="ICoreAISettings.MaxToolCallHistoryMessages"/>).
        /// </summary>
        public static int Trim(List<MEAI.ChatMessage> messages, int maxToolMessages)
        {
            if (messages == null || maxToolMessages <= 0)
            {
                return 0;
            }

            int toolMessageCount = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role == MEAI.ChatRole.Tool)
                {
                    toolMessageCount++;
                }
                else if (messages[i].Role == MEAI.ChatRole.Assistant && HasFunctionCallContent(messages[i]))
                {
                    toolMessageCount++;
                }
            }

            if (toolMessageCount <= maxToolMessages)
            {
                return 0;
            }

            int toRemove = toolMessageCount - maxToolMessages;
            int removed = 0;

            // WHY: A limit below one Assistant+Tool pair must still retain the latest result, including
            // WHY: its image feedback, so a newly captured frame reaches the next provider request.
            MEAI.ChatMessage newestToolAssistant = null;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].Role == MEAI.ChatRole.Assistant && HasFunctionCallContent(messages[i]))
                {
                    newestToolAssistant = messages[i];
                    break;
                }
            }

            // WHY: Remove oldest tool-call units as coupled blocks. Each unit starts at an Assistant
            // tool-call message and extends through every Tool result message that immediately
            // follows it. Removing the unit as a whole keeps every surviving Tool message paired
            // with its preceding Assistant tool_calls message (OpenAI-valid). We may overshoot the
            // exact target by at most one unit, but never split a unit, since splitting produces the
            // orphaned-tool-message HTTP 400 this method exists to prevent.
            int index = 0;
            while (index < messages.Count && removed < toRemove)
            {
                if (object.ReferenceEquals(messages[index], newestToolAssistant))
                {
                    break;
                }

                bool isToolAssistant =
                    messages[index].Role == MEAI.ChatRole.Assistant && HasFunctionCallContent(messages[index]);

                if (isToolAssistant)
                {
                    int unitToolMessages = 1;

                    messages.RemoveAt(index);
                    while (index < messages.Count && messages[index].Role == MEAI.ChatRole.Tool)
                    {
                        messages.RemoveAt(index);
                        unitToolMessages++;
                    }

                    // WHY: tool images are inserted as User messages right after their exchange because OpenAI tool results cannot carry image parts, so an old image is retired together with its source tool exchange.
                    while (index < messages.Count && ImageFeedbackMessages.TryGetValue(messages[index], out _))
                    {
                        messages.RemoveAt(index);
                    }

                    removed += unitToolMessages;
                }
                else
                {
                    // WHY: Preserve non-tool messages (system/user/plain assistant) and skip past them.
                    // A leading Tool message without a preceding Assistant tool-call would already be
                    // malformed; leave it untouched rather than orphan it further.
                    index++;
                }
            }

            return removed;
        }

        /// <summary>
        /// Removes obsolete error-feedback messages (tracked failed Assistant tool-call turns and
        /// their paired Tool result turns) from <paramref name="messages"/> after a successful retry.
        /// Removal is by reference and always covers the full Assistant+Tool pair, so the remaining
        /// history keeps every tool-call message paired with its tool-result message (OpenAI-valid).
        /// Entries already trimmed by the general history trim are skipped silently.
        /// Clears <paramref name="feedbackMessages"/> and returns how many messages were removed.
        /// </summary>
        public static int RemoveResolvedErrorFeedback(
            List<MEAI.ChatMessage> messages,
            List<MEAI.ChatMessage> feedbackMessages)
        {
            int removed = 0;
            foreach (MEAI.ChatMessage feedback in feedbackMessages)
            {
                if (messages.Remove(feedback))
                {
                    removed++;
                }
            }

            feedbackMessages.Clear();
            return removed;
        }

        /// <summary>Whether the message carries at least one <see cref="MEAI.FunctionCallContent"/>.</summary>
        public static bool HasFunctionCallContent(MEAI.ChatMessage message)
        {
            if (message?.Contents == null)
            {
                return false;
            }

            foreach (object item in message.Contents)
            {
                if (item is MEAI.FunctionCallContent)
                {
                    return true;
                }
            }

            return false;
        }

    }
}
#endif
