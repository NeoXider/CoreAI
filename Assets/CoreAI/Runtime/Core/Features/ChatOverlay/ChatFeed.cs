using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.ChatOverlay
{
    /// <summary>Who wrote a feed line; the view styles each kind differently.</summary>
    public enum ChatFeedLineKind
    {
        /// <summary>The local player.</summary>
        Player = 0,

        /// <summary>The AI agent's answer.</summary>
        Assistant = 1,

        /// <summary>Game, NPC or system text.</summary>
        System = 2,

        /// <summary>A slash command's reply.</summary>
        Command = 3,

        /// <summary>A failure shown to the player.</summary>
        Error = 4
    }

    /// <summary>One line of the in-world chat feed. A streamed answer is one line that grows in place.</summary>
    public sealed class ChatFeedLine
    {
        private readonly StringBuilder _text = new();
        private string _cachedText = "";
        private bool _cacheValid = true;

        internal ChatFeedLine(long id, ChatFeedLineKind kind, string speaker, double now)
        {
            Id = id;
            Kind = kind;
            Speaker = speaker ?? "";
            CreatedAt = now;
            LastActivityAt = now;
        }

        /// <summary>Unique, increasing id within one feed.</summary>
        public long Id { get; }

        /// <summary>Who wrote the line.</summary>
        public ChatFeedLineKind Kind { get; }

        /// <summary>Name shown before the text; empty for none.</summary>
        public string Speaker { get; }

        /// <summary>The line's text so far.</summary>
        public string Text
        {
            get
            {
                if (!_cacheValid)
                {
                    _cachedText = _text.ToString();
                    _cacheValid = true;
                }

                return _cachedText;
            }
        }

        /// <summary>True while an answer is still streaming into this line.</summary>
        public bool IsStreaming { get; internal set; }

        /// <summary>True once the feed dropped the line; later appends are ignored.</summary>
        public bool IsRemoved { get; internal set; }

        /// <summary>When the line was added (chat clock seconds).</summary>
        public double CreatedAt { get; }

        /// <summary>When the line last changed; the fade delay counts from here.</summary>
        public double LastActivityAt { get; internal set; }

        /// <summary>True once the text reached the feed's per-line limit and later text is dropped.</summary>
        public bool IsTruncated { get; private set; }

        internal int Length => _text.Length;

        internal bool AppendLimited(string text, int maxCharacters)
        {
            if (string.IsNullOrEmpty(text) || IsTruncated)
            {
                return false;
            }

            if (maxCharacters > 0 && _text.Length + text.Length > maxCharacters)
            {
                int room = Math.Max(0, maxCharacters - _text.Length);
                _text.Append(text, 0, room);
                _text.Append('\u2026');
                IsTruncated = true;
            }
            else
            {
                _text.Append(text);
            }

            _cacheValid = false;
            return true;
        }
    }

    /// <summary>
    /// The model of the in-world chat feed: an ordered, capped list of lines. Engine-free; the controller turns
    /// its events into view calls.
    /// </summary>
    public sealed class ChatFeed
    {
        private readonly List<ChatFeedLine> _lines = new();
        private long _nextId = 1;

        /// <summary>Creates a feed that keeps at most <paramref name="maxLines"/> lines (at least 1).</summary>
        public ChatFeed(int maxLines = 100, int maxLineCharacters = 4000)
        {
            MaxLines = Math.Max(1, maxLines);
            MaxLineCharacters = Math.Max(0, maxLineCharacters);
        }

        /// <summary>Most lines kept; adding more drops the oldest.</summary>
        public int MaxLines { get; }

        /// <summary>Longest text of one line; 0 is unlimited.</summary>
        public int MaxLineCharacters { get; }

        /// <summary>The lines, oldest first.</summary>
        public IReadOnlyList<ChatFeedLine> Lines => _lines;

        /// <summary>Raised after a line was added.</summary>
        public event Action<ChatFeedLine> LineAdded;

        /// <summary>Raised after a line's text or streaming state changed.</summary>
        public event Action<ChatFeedLine> LineChanged;

        /// <summary>Raised after a line was dropped.</summary>
        public event Action<ChatFeedLine> LineRemoved;

        /// <summary>Adds a complete line.</summary>
        public ChatFeedLine Add(ChatFeedLineKind kind, string speaker, string text, double now)
        {
            ChatFeedLine line = new(_nextId++, kind, speaker, now);
            line.AppendLimited(text ?? "", MaxLineCharacters);
            Insert(line);
            return line;
        }

        /// <summary>Adds an empty line that an answer streams into; finish it with <see cref="Complete"/>.</summary>
        public ChatFeedLine BeginStreaming(ChatFeedLineKind kind, string speaker, double now)
        {
            ChatFeedLine line = new(_nextId++, kind, speaker, now) { IsStreaming = true };
            Insert(line);
            return line;
        }

        /// <summary>Appends text to a line in place.</summary>
        public void Append(ChatFeedLine line, string text, double now)
        {
            if (line == null || line.IsRemoved || string.IsNullOrEmpty(text))
            {
                return;
            }

            if (line.AppendLimited(text, MaxLineCharacters))
            {
                line.LastActivityAt = now;
                LineChanged?.Invoke(line);
            }
        }

        /// <summary>Marks a streamed line finished; the fade delay starts now.</summary>
        public void Complete(ChatFeedLine line, double now)
        {
            if (line == null || line.IsRemoved || !line.IsStreaming)
            {
                return;
            }

            line.IsStreaming = false;
            line.LastActivityAt = now;
            LineChanged?.Invoke(line);
        }

        /// <summary>Drops every line.</summary>
        public void Clear()
        {
            while (_lines.Count > 0)
            {
                RemoveAt(0);
            }
        }

        private void Insert(ChatFeedLine line)
        {
            _lines.Add(line);
            LineAdded?.Invoke(line);
            while (_lines.Count > MaxLines)
            {
                RemoveAt(0);
            }
        }

        private void RemoveAt(int index)
        {
            ChatFeedLine removed = _lines[index];
            _lines.RemoveAt(index);
            removed.IsRemoved = true;
            removed.IsStreaming = false;
            LineRemoved?.Invoke(removed);
        }
    }

    /// <summary>The fade rule of the in-world chat.</summary>
    public static class ChatFeedVisibility
    {
        /// <summary>
        /// A line is shown while the input is open, while it is streaming, and for
        /// <paramref name="fadeDelaySeconds"/> after it last changed. A delay of zero or less never fades.
        /// </summary>
        public static bool IsVisible(ChatFeedLine line, double now, bool inputOpen, double fadeDelaySeconds)
        {
            if (line == null)
            {
                return false;
            }

            if (inputOpen || line.IsStreaming || fadeDelaySeconds <= 0d)
            {
                return true;
            }

            return now - line.LastActivityAt < fadeDelaySeconds;
        }
    }
}
