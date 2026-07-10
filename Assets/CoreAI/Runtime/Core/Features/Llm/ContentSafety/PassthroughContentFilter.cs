namespace CoreAI.Ai
{
    /// <summary>
    /// Default no-op <see cref="IContentFilter"/>: every text is allowed unchanged, both directions.
    /// Use as the wiring default so call sites never need a null check. Stateless and thread-safe.
    /// </summary>
    public sealed class PassthroughContentFilter : IContentFilter
    {
        /// <summary>Shared singleton (the class is stateless).</summary>
        public static readonly PassthroughContentFilter Instance = new();

        private PassthroughContentFilter()
        {
        }

        /// <inheritdoc />
        public ContentFilterVerdict Evaluate(string text, ContentFilterContext context)
        {
            return ContentFilterVerdict.Allow;
        }
    }
}
