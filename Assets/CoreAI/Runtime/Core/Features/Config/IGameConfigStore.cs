namespace CoreAI.Config
{
    /// <summary>
    /// Defines persistence operations for game configuration payloads.
    /// </summary>
    public interface IGameConfigStore
    {
        /// <summary>
/// Executes TryLoad API operation.
        /// </summary>
        /// <param name="key">The key value.</param>
        /// <param name="json">The json value.</param>
        /// <returns>The operation result.</returns>
        bool TryLoad(string key, out string json);

        /// <summary>
/// Executes TrySave API operation.
        /// </summary>
        bool TrySave(string key, string json);

        /// <summary>
/// Executes GetKnownKeys API operation.
        /// </summary>
        string[] GetKnownKeys();
    }
}
