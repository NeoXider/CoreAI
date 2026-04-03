namespace CoreAI.Ai
{
    /// <summary>Колбэки жизненного цикла исполнения Lua из <see cref="LuaAiEnvelopeProcessor"/>.</summary>
    public interface ILuaExecutionObserver
    {
        /// <summary>Чанк Lua выполнен успешно.</summary>
        void OnLuaSuccess(string resultSummary);

        /// <summary>Ошибка MoonSharp или логики песочницы.</summary>
        void OnLuaFailure(string errorMessage);

        /// <summary>Запланирован повторный вызов Programmer с увеличенным поколением ремонта.</summary>
        void OnLuaRepairScheduled(int nextGeneration, string errorPreview);
    }

    /// <summary>Пустая реализация наблюдателя.</summary>
    public sealed class NullLuaExecutionObserver : ILuaExecutionObserver
    {
        /// <inheritdoc />
        public void OnLuaSuccess(string resultSummary)
        {
        }

        /// <inheritdoc />
        public void OnLuaFailure(string errorMessage)
        {
        }

        /// <inheritdoc />
        public void OnLuaRepairScheduled(int nextGeneration, string errorPreview)
        {
        }
    }
}
