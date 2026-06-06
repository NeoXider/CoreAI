namespace CoreAI.Session
{
    /// <summary>
    /// Immutable session data passed into prompt composition.
    /// </summary>
    public sealed class GameSessionSnapshot
    {
        /// <summary>
        /// String.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string> Telemetry { get; } =
            new();
    }
}