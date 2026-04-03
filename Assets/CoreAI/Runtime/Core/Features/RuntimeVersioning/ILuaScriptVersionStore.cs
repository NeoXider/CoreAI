using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Версионирование Lua, которые выдаёт Programmer: исходный (первый успешный или заданный seed),
    /// текущий последний успешный, история; сброс к исходнику.
    /// </summary>
    public interface ILuaScriptVersionStore
    {
        /// <summary>Прочитать состояние слота; <c>false</c> — слота ещё не было.</summary>
        bool TryGetSnapshot(string scriptKey, out LuaScriptVersionRecord snapshot);

        /// <summary>
        /// Зафиксировать успешное выполнение Lua из конверта (после MoonSharp без ошибки).
        /// Первый вызов для ключа задаёт и original, и current.
        /// </summary>
        void RecordSuccessfulExecution(string scriptKey, string executedLuaSource);

        /// <summary>
        /// Задать исходный вариант до первого запуска (например из ассета игры).
        /// Если слот уже есть и <paramref name="overwriteExistingOriginal"/> ложь — original не перезаписывается.
        /// </summary>
        void SeedOriginal(string scriptKey, string originalLuaSource, bool overwriteExistingOriginal = false);

        /// <summary>Текущий код = исходный; история сокращается до одной ревизии-оригинала.</summary>
        void ResetToOriginal(string scriptKey);

        /// <summary>Сброс всех слотов к baseline (как <see cref="ResetToOriginal"/> по каждому известному ключу).</summary>
        void ResetAllToOriginal();

        /// <summary>Ключи слотов с ненулевой историей (для отладки, Lua API, дашборда).</summary>
        IReadOnlyList<string> GetKnownKeys();

        /// <summary>Фрагмент для user-промпта Programmer (пусто, если ключ пустой или store отключён).</summary>
        string BuildProgrammerPromptSection(string scriptKey);
    }
}
