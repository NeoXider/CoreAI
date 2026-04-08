namespace CoreAI.Ai
{
    /// <summary>
    /// Действия для MemoryTool.
    /// </summary>
    public enum MemoryToolAction
    {
        /// <summary>Полная замена памяти (перезаписать всё).</summary>
        Write = 0,

        /// <summary>Добавление к существующей памяти.</summary>
        Append = 1,

        /// <summary>Очистка всей памяти.</summary>
        Clear = 2
    }
}