namespace CoreAI.Ai.Npc
{
    /// <summary>Which side of a model call a text is on.</summary>
    public enum TextModerationDirection
    {
        /// <summary>Text a player or a script sends to the model.</summary>
        Input = 0,

        /// <summary>Text the model produced, before anyone sees it.</summary>
        Output = 1
    }

    /// <summary>
    /// Moderation seam for gameplay model calls (NPC lines, <c>TextGenerator</c>). The default passes every text;
    /// a host shipping on a platform with content rules plugs in its filter.
    /// </summary>
    public interface ITextModerationFilter
    {
        /// <summary>
        /// Returns the text to use, possibly rewritten; false refuses it, and the caller then uses an
        /// in-character fallback (NPC) or a typed failure (<c>TextGenerator</c>).
        /// </summary>
        bool TryFilter(string text, TextModerationDirection direction, out string filtered);
    }

    /// <summary>The default moderation filter: every text passes unchanged.</summary>
    public sealed class PassThroughTextModerationFilter : ITextModerationFilter
    {
        /// <summary>Shared instance; the type has no state.</summary>
        public static readonly PassThroughTextModerationFilter Instance = new();

        /// <inheritdoc />
        public bool TryFilter(string text, TextModerationDirection direction, out string filtered)
        {
            filtered = text ?? "";
            return true;
        }
    }
}
