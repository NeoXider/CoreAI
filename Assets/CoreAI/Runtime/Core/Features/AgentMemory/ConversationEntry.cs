namespace CoreAI.Ai
{
    /// <summary>Type of persisted transcript entry.</summary>
    public enum ConversationEntryKind
    {
        /// <summary>User-visible line.</summary>
        User = 0,

        /// <summary>Assistant visible line.</summary>
        Assistant = 1,

        /// <summary>Tool invocation envelope (optional).</summary>
        ToolCall = 2,

        /// <summary>Tool outcome text (may be abbreviated).</summary>
        ToolResult = 3
    }

    /// <summary>Optional structured transcript for pruning and diagnostics.</summary>
    public sealed class ConversationEntry
    {
        /// <summary>See <see cref="ConversationEntryKind"/>.</summary>
        public ConversationEntryKind Kind { get; set; }

        /// <summary>Speaker role or tool identifier.</summary>
        public string Key { get; set; } = "";

        /// <summary>Body text or abbreviated payload.</summary>
        public string Content { get; set; } = "";

        /// <summary>Provider correlation id when known.</summary>
        public string CallId { get; set; } = "";

        /// <summary>Unix ms when written.</summary>
        public long Timestamp { get; set; }
    }
}