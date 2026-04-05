namespace CoreAI.Config
{
    /// <summary>
    /// Абстрактное хранилище игровых конфигов (JSON по ключу).
    /// Реализуется игрой: файлы, ScriptableObject, база данных и т.д.
    /// </summary>
    public interface IGameConfigStore
    {
        /// <summary>
        /// Загружает конфиг по ключу как JSON строку.
        /// </summary>
        /// <param name="key">Ключ конфига (например "session", "crafting", "balance").</param>
        /// <param name="json">JSON строка конфига или null если не найден.</param>
        /// <returns>true если конфиг найден.</returns>
        bool TryLoad(string key, out string json);

        /// <summary>
        /// Сохраняет конфиг по ключу из JSON строки.
        /// </summary>
        bool TrySave(string key, string json);

        /// <summary>
        /// Возвращает список доступных ключей конфигов.
        /// </summary>
        string[] GetKnownKeys();
    }
}
