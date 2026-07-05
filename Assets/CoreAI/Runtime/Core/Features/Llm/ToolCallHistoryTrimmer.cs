#if !COREAI_NO_LLM
using System.Collections.Generic;
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

            // Count tool-related messages: any with role Tool, or Assistant with FunctionCallContent.
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

            // Remove oldest tool-call units as coupled blocks. Each unit starts at an Assistant
            // tool-call message and extends through every Tool result message that immediately
            // follows it. Removing the unit as a whole keeps every surviving Tool message paired
            // with its preceding Assistant tool_calls message (OpenAI-valid). We may overshoot the
            // exact target by at most one unit, but never split a unit, since splitting produces the
            // orphaned-tool-message HTTP 400 this method exists to prevent.
            int index = 0;
            while (index < messages.Count && removed < toRemove)
            {
                bool isToolAssistant =
                    messages[index].Role == MEAI.ChatRole.Assistant && HasFunctionCallContent(messages[index]);

                if (isToolAssistant)
                {
                    int unitToolMessages = 1; // the Assistant tool-call message itself

                    // Drop the Assistant tool-call message, then every contiguous Tool result it owns.
                    messages.RemoveAt(index);
                    while (index < messages.Count && messages[index].Role == MEAI.ChatRole.Tool)
                    {
                        messages.RemoveAt(index);
                        unitToolMessages++;
                    }

                    removed += unitToolMessages;
                }
                else
                {
                    // Preserve non-tool messages (system/user/plain assistant) and skip past them.
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