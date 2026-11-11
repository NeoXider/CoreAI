using System;
using System.Collections.Generic;
using CoreAI.Logging;

namespace CoreAI.Ai.Npc
{
    /// <summary>One line an NPC is speaking, as the speech sinks see it.</summary>
    public readonly struct NpcSpeech
    {
        /// <summary>Creates the description.</summary>
        public NpcSpeech(string npcId, string npcName, string speakerId, string speakerName, long turnId,
            string originTag)
        {
            NpcId = npcId ?? "";
            NpcName = npcName ?? "";
            SpeakerId = speakerId ?? "";
            SpeakerName = speakerName ?? "";
            TurnId = turnId;
            OriginTag = originTag ?? "";
        }

        /// <summary>The speaking NPC.</summary>
        public string NpcId { get; }

        /// <summary>Its display name.</summary>
        public string NpcName { get; }

        /// <summary>Who it is answering; empty for a scripted line.</summary>
        public string SpeakerId { get; }

        /// <summary>The name of who it is answering.</summary>
        public string SpeakerName { get; }

        /// <summary>The model turn; 0 for a scripted line.</summary>
        public long TurnId { get; }

        /// <summary>Origin tag of the NPC; empty for a host NPC.</summary>
        public string OriginTag { get; }
    }

    /// <summary>
    /// Where NPC speech goes: the in-world chat feed, a bubble over the head, a voice, a log. The director calls
    /// every sink on the thread that owns it, in this order per line: <see cref="SpeechStarted"/>, any number of
    /// <see cref="SpeechGrew"/>, then exactly one of <see cref="SpeechCompleted"/> or <see cref="SpeechCancelled"/>.
    /// A scripted line skips straight to <see cref="SpeechCompleted"/>.
    /// </summary>
    public interface INpcSpeechSink
    {
        /// <summary>The NPC started thinking about an answer.</summary>
        void SpeechStarted(NpcSpeech speech);

        /// <summary>The streamed answer grew; <paramref name="textSoFar"/> is everything said so far.</summary>
        void SpeechGrew(NpcSpeech speech, string textSoFar);

        /// <summary>The line is final. <paramref name="isFallback"/> is true for an in-character fallback line.</summary>
        void SpeechCompleted(NpcSpeech speech, string text, bool isFallback);

        /// <summary>The line was abandoned (a newer line replaced it, or the NPC was removed); take back what was shown.</summary>
        void SpeechCancelled(NpcSpeech speech);
    }

    /// <summary>
    /// Fans NPC speech out to several sinks. A sink that throws is logged and skipped, so a broken bubble never
    /// silences the chat.
    /// </summary>
    public sealed class CompositeNpcSpeechSink : INpcSpeechSink
    {
        private readonly List<INpcSpeechSink> _sinks = new();
        private readonly object _gate = new();
        private readonly ILog _log;

        /// <summary>Creates the fan-out with optional initial sinks.</summary>
        public CompositeNpcSpeechSink(IEnumerable<INpcSpeechSink> sinks = null, ILog log = null)
        {
            _log = log ?? NullLog.Instance;
            if (sinks == null)
            {
                return;
            }

            foreach (INpcSpeechSink sink in sinks)
            {
                Add(sink);
            }
        }

        /// <summary>Number of attached sinks.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _sinks.Count;
                }
            }
        }

        /// <summary>Attaches a sink; adding the same sink twice keeps one.</summary>
        public void Add(INpcSpeechSink sink)
        {
            if (sink == null || ReferenceEquals(sink, this))
            {
                return;
            }

            lock (_gate)
            {
                if (!_sinks.Contains(sink))
                {
                    _sinks.Add(sink);
                }
            }
        }

        /// <summary>Detaches a sink; false when it was not attached.</summary>
        public bool Remove(INpcSpeechSink sink)
        {
            lock (_gate)
            {
                return _sinks.Remove(sink);
            }
        }

        /// <inheritdoc />
        public void SpeechStarted(NpcSpeech speech)
        {
            ForEach(s => s.SpeechStarted(speech), nameof(SpeechStarted));
        }

        /// <inheritdoc />
        public void SpeechGrew(NpcSpeech speech, string textSoFar)
        {
            ForEach(s => s.SpeechGrew(speech, textSoFar), nameof(SpeechGrew));
        }

        /// <inheritdoc />
        public void SpeechCompleted(NpcSpeech speech, string text, bool isFallback)
        {
            ForEach(s => s.SpeechCompleted(speech, text, isFallback), nameof(SpeechCompleted));
        }

        /// <inheritdoc />
        public void SpeechCancelled(NpcSpeech speech)
        {
            ForEach(s => s.SpeechCancelled(speech), nameof(SpeechCancelled));
        }

        private void ForEach(Action<INpcSpeechSink> call, string what)
        {
            INpcSpeechSink[] snapshot;
            lock (_gate)
            {
                snapshot = _sinks.ToArray();
            }

            foreach (INpcSpeechSink sink in snapshot)
            {
                try
                {
                    call(sink);
                }
                catch (Exception ex)
                {
                    _log.Warn($"[Npc] Speech sink {sink.GetType().Name}.{what} threw: {ex.Message}", LogTag.Llm);
                }
            }
        }
    }
}
