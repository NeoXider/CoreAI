using System;
using System.Collections.Generic;

namespace CoreAI.Ai.Npc
{
    /// <summary>The state of one NPC's speech bubble, read by a view each frame.</summary>
    public sealed class NpcSpeechBubble
    {
        internal NpcSpeechBubble(string npcId)
        {
            NpcId = npcId;
        }

        /// <summary>The NPC the bubble floats over.</summary>
        public string NpcId { get; }

        /// <summary>The NPC's display name.</summary>
        public string NpcName { get; internal set; } = "";

        /// <summary>The text shown; while the NPC thinks it is <see cref="NpcSpeechBubbleOptions.ThinkingText"/>.</summary>
        public string Text { get; internal set; } = "";

        /// <summary>True while the NPC has started an answer and no text arrived yet.</summary>
        public bool IsThinking { get; internal set; }

        /// <summary>True while text is still streaming in.</summary>
        public bool IsStreaming { get; internal set; }

        /// <summary>True when the text is an in-character fallback line.</summary>
        public bool IsFallback { get; internal set; }

        /// <summary>True while the bubble should be drawn.</summary>
        public bool IsVisible { get; internal set; }

        /// <summary>Bumped on every change, so a view redraws only what changed.</summary>
        public int Version { get; internal set; }

        /// <summary>The turn the bubble shows; 0 for a scripted line.</summary>
        public long TurnId { get; internal set; }

        /// <summary>When the bubble's line became final, on the board's clock.</summary>
        public double CompletedAt { get; internal set; } = double.NaN;

        /// <summary>When the bubble hides itself if nothing new is said, on the board's clock.</summary>
        public double HideAt { get; internal set; } = double.PositiveInfinity;
    }

    /// <summary>How long bubbles stay and how they look while the NPC thinks, as data.</summary>
    public sealed class NpcSpeechBubbleOptions
    {
        /// <summary>Text shown while the NPC thinks; empty keeps the bubble hidden until text arrives.</summary>
        public string ThinkingText { get; set; } = "...";

        /// <summary>Seconds a finished line stays at least.</summary>
        public double MinHoldSeconds { get; set; } = 3d;

        /// <summary>Extra seconds per character of the finished line, so a long line stays long enough to read.</summary>
        public double HoldSecondsPerCharacter { get; set; } = 0.06d;

        /// <summary>Longest time a finished line stays.</summary>
        public double MaxHoldSeconds { get; set; } = 12d;

        /// <summary>Longest text a bubble shows; the end of a longer line is kept (it is what is being said now).</summary>
        public int MaxCharacters { get; set; } = 220;
    }

    /// <summary>
    /// Engine-free model of the speech bubbles over NPC heads, fed as an <see cref="INpcSpeechSink"/>: a bubble
    /// shows "..." while its NPC thinks, grows as the answer streams in, stays for a time that depends on the
    /// line's length and then hides. A Unity view (or any renderer) draws <see cref="Bubbles"/> and calls
    /// <see cref="Tick"/>. Called on the thread that owns the view.
    /// </summary>
    public sealed class NpcSpeechBubbleBoard : INpcSpeechSink
    {
        private readonly Dictionary<string, NpcSpeechBubble> _bubbles = new(StringComparer.Ordinal);
        private readonly List<NpcSpeechBubble> _ordered = new();
        private readonly NpcSpeechBubbleOptions _options;
        private readonly INpcClock _clock;

        /// <summary>Creates the board.</summary>
        public NpcSpeechBubbleBoard(NpcSpeechBubbleOptions options = null, INpcClock clock = null)
        {
            _options = options ?? new NpcSpeechBubbleOptions();
            _clock = clock ?? new StopwatchNpcClock();
        }

        /// <summary>Raised when a bubble's state changed (text, visibility).</summary>
        public event Action<NpcSpeechBubble> BubbleChanged;

        /// <summary>Every bubble the board knows, visible or not, in the order their NPCs first spoke.</summary>
        public IReadOnlyList<NpcSpeechBubble> Bubbles => _ordered;

        /// <summary>The bubble of <paramref name="npcId"/>, when its NPC has spoken.</summary>
        public bool TryGet(string npcId, out NpcSpeechBubble bubble)
        {
            return _bubbles.TryGetValue(npcId ?? "", out bubble);
        }

        /// <summary>Forgets the bubble of an NPC that left the world.</summary>
        public bool Forget(string npcId)
        {
            if (!_bubbles.TryGetValue(npcId ?? "", out NpcSpeechBubble bubble))
            {
                return false;
            }

            _bubbles.Remove(bubble.NpcId);
            _ordered.Remove(bubble);
            if (bubble.IsVisible)
            {
                bubble.IsVisible = false;
                Changed(bubble);
            }

            return true;
        }

        /// <inheritdoc />
        public void SpeechStarted(NpcSpeech speech)
        {
            NpcSpeechBubble bubble = GetOrCreate(speech);
            bubble.TurnId = speech.TurnId;
            bubble.IsThinking = true;
            bubble.IsStreaming = true;
            bubble.IsFallback = false;
            bubble.CompletedAt = double.NaN;
            bubble.HideAt = double.PositiveInfinity;
            bubble.Text = _options.ThinkingText ?? "";
            bubble.IsVisible = bubble.Text.Length > 0;
            Changed(bubble);
        }

        /// <inheritdoc />
        public void SpeechGrew(NpcSpeech speech, string textSoFar)
        {
            if (!_bubbles.TryGetValue(speech.NpcId, out NpcSpeechBubble bubble) || bubble.TurnId != speech.TurnId ||
                !bubble.IsStreaming || string.IsNullOrEmpty(textSoFar))
            {
                return;
            }

            bubble.IsThinking = false;
            bubble.Text = Tail(textSoFar);
            bubble.IsVisible = true;
            Changed(bubble);
        }

        /// <inheritdoc />
        public void SpeechCompleted(NpcSpeech speech, string text, bool isFallback)
        {
            NpcSpeechBubble bubble = GetOrCreate(speech);
            bool scripted = speech.TurnId == 0;
            if (!scripted && bubble.TurnId != speech.TurnId)
            {
                return;
            }

            double now = _clock.NowSeconds;
            string shown = Tail((text ?? "").Trim());
            bubble.TurnId = speech.TurnId;
            bubble.IsThinking = false;
            bubble.IsStreaming = false;
            bubble.IsFallback = isFallback;
            bubble.Text = shown;
            bubble.IsVisible = shown.Length > 0;
            bubble.CompletedAt = now;
            bubble.HideAt = now + HoldSeconds(shown.Length);
            Changed(bubble);
        }

        /// <inheritdoc />
        public void SpeechCancelled(NpcSpeech speech)
        {
            if (!_bubbles.TryGetValue(speech.NpcId, out NpcSpeechBubble bubble) || bubble.TurnId != speech.TurnId)
            {
                return;
            }

            bubble.IsThinking = false;
            bubble.IsStreaming = false;
            bubble.IsVisible = false;
            bubble.HideAt = double.PositiveInfinity;
            Changed(bubble);
        }

        /// <summary>Hides bubbles whose hold time passed. Call once per frame.</summary>
        public void Tick()
        {
            double now = _clock.NowSeconds;
            foreach (NpcSpeechBubble bubble in _ordered)
            {
                if (bubble.IsVisible && !bubble.IsStreaming && now >= bubble.HideAt)
                {
                    bubble.IsVisible = false;
                    Changed(bubble);
                }
            }
        }

        /// <summary>Seconds a finished line of <paramref name="characters"/> characters stays.</summary>
        public double HoldSeconds(int characters)
        {
            double hold = _options.MinHoldSeconds + Math.Max(0, characters) * _options.HoldSecondsPerCharacter;
            return _options.MaxHoldSeconds > 0d ? Math.Min(hold, Math.Max(_options.MinHoldSeconds, _options.MaxHoldSeconds)) : hold;
        }

        private NpcSpeechBubble GetOrCreate(NpcSpeech speech)
        {
            if (!_bubbles.TryGetValue(speech.NpcId, out NpcSpeechBubble bubble))
            {
                bubble = new NpcSpeechBubble(speech.NpcId);
                _bubbles.Add(speech.NpcId, bubble);
                _ordered.Add(bubble);
            }

            bubble.NpcName = speech.NpcName;
            return bubble;
        }

        private string Tail(string text)
        {
            int max = _options.MaxCharacters;
            if (max <= 0 || text.Length <= max)
            {
                return text;
            }

            int start = text.Length - (max - 1);
            if (start < text.Length && char.IsLowSurrogate(text[start]))
            {
                start++;
            }

            return "…" + text.Substring(start);
        }

        private void Changed(NpcSpeechBubble bubble)
        {
            bubble.Version++;
            BubbleChanged?.Invoke(bubble);
        }
    }
}
