namespace CoreAI.Config
{
    /// <summary>
    /// Заглушка хранилища конфигов (по умолчанию: ничего не хранит).
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