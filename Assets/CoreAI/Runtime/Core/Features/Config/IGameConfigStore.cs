namespace CoreAI.Config
{
    /// <summary>
    /// Defines persistence operations for game configuration payloads.
    /// </summary>
    public interface IGameConfigStore
    {
        /// <summary>
        /// Loads a JSON configuration payload by key.
        /// </summary>
        /// <param name="key">Stable configuration key.</param>
        /// <param name="json">Loaded JSON payload when the key exists.</param>
        /// <returns><c>true</c> when a payload was found.</returns>
        bool TryLoad(string key, out string json);

        /// <summary>
        /// Saves a JSON configuration payload by key.
        /// </summary>
        bool TrySave(string key, string json);

        /// <summary>
        /// Returns configuration keys known to this store.
        /// </summary>
        string[] GetKnownKeys();
    }
}
