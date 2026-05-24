namespace CoreAI.Config
{
    /// <summary>
    /// No-op game configuration store used when config persistence is unavailable.
    /// </summary>
    public sealed class NullGameConfigStore : IGameConfigStore
    {
        /// <inheritdoc />
        public bool TryLoad(string key, out string json)
        {
            json = null;
            return false;
        }

        /// <inheritdoc />
        public bool TrySave(string key, string json)
        {
            return false;
        }

        /// <inheritdoc />
        public string[] GetKnownKeys()
        {
            return System.Array.Empty<string>();
        }
    }
}
