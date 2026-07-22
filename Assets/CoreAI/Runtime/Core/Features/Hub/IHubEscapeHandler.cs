namespace CoreAI.Hub
{
    /// <summary>
    /// Optional hook for an <see cref="IHubPage"/> that wants first refusal on Escape while it is the
    /// active page — e.g. stopping an in-flight AI request instead of letting the Hub collapse.
    /// </summary>
    public interface IHubEscapeHandler
    {
        /// <summary>
        /// Called when Escape is pressed while this page is active and the Hub is expanded. Return
        /// <c>true</c> to consume the key (the Hub stays expanded); return <c>false</c> to let the Hub
        /// collapse as usual.
        /// </summary>
        bool TryHandleEscape();
    }
}
