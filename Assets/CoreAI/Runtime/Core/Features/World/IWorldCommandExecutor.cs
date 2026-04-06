namespace CoreAI.Ai
{
    /// <summary>
    /// Абстрактный интерфейс для выполнения world commands.
    /// Реализуется для каждого движка отдельно (Unity, Unreal, Godot).
    /// 
    /// Этот интерфейс не зависит от движка и определяет контракт
    /// для выполнения команд управления миром (spawn, move, destroy, etc.).
    /// </summary>
    public interface IWorldCommandExecutor
    {
        /// <summary>
        /// Выполнить команду мира.
        /// </summary>
        /// <param name="action">Тип команды: spawn, move, destroy, load_scene, etc.</param>
        /// <param name="parameters">JSON параметры команды</param>
        /// <returns>true если команда выполнена успешно</returns>
        bool TryExecute(string action, string parameters);
    }
}
