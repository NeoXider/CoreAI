namespace CoreAI.Ai
{
    /// <summary>
    /// Абстрактный интерфейс для управления UI.
    /// Реализуется для каждого движка отдельно (Unity, Unreal, Godot).
    /// </summary>
    public interface IUIController
    {
        /// <summary>
        /// Показать текстовое уведомление.
        /// </summary>
        /// <param name="targetName">Имя UI элемента</param>
        /// <param name="text">Текст для отображения</param>
        /// <param name="durationSeconds">Длительность отображения (секунды)</param>
        /// <returns>true если уведомление показано</returns>
        bool ShowText(string targetName, string text, float durationSeconds = 2f);

        /// <summary>
        /// Скрыть UI элемент.
        /// </summary>
        /// <param name="targetName">Имя UI элемента</param>
        bool Hide(string targetName);

        /// <summary>
        /// Показать/скрыть UI элемент.
        /// </summary>
        /// <param name="targetName">Имя UI элемента</param>
        /// <param name="visible">true = показать, false = скрыть</param>
        bool SetVisible(string targetName, bool visible);
    }
}
