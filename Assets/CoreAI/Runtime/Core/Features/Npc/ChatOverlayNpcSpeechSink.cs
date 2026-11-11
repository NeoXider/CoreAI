using System;
using CoreAI.ChatOverlay;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// Posts every finished NPC line into the in-world chat feed through <see cref="IChatOverlayMessages"/>, with
    /// the NPC's name as the speaker. Streaming is left to the bubble: the feed gets one complete line per answer,
    /// so the chat history reads like a conversation. Safe from any thread (the overlay queues foreign-thread posts).
    /// </summary>
    public sealed class ChatOverlayNpcSpeechSink : INpcSpeechSink
    {
        private readonly IChatOverlayMessages _messages;
        private readonly ChatFeedLineKind _kind;

        /// <summary>Creates the sink over the overlay's message port.</summary>
        public ChatOverlayNpcSpeechSink(IChatOverlayMessages messages, ChatFeedLineKind kind = ChatFeedLineKind.System)
        {
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _kind = kind;
        }

        /// <inheritdoc />
        public void SpeechStarted(NpcSpeech speech)
        {
        }

        /// <inheritdoc />
        public void SpeechGrew(NpcSpeech speech, string textSoFar)
        {
        }

        /// <inheritdoc />
        public void SpeechCompleted(NpcSpeech speech, string text, bool isFallback)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            _messages.PostMessage(speech.NpcName, text.Trim(), _kind);
        }

        /// <inheritdoc />
        public void SpeechCancelled(NpcSpeech speech)
        {
        }
    }
}
