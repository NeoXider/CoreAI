namespace CoreAI.Session
{
    /// <summary>
    /// Снимок данных сессии для подстановки в user-prompt: в первую очередь словарь телеметрии, который наполняет игра.
    /// </summary>
    public sealed class GameSessionSnapshot
    {
        /// <summary>
        /// Игронезависимая телеметрия, которую игра сама обновляет в Core.
        /// Ключи и значения определяет тайтл (например: "wave", "player.hp.current", "player.style", ...).
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string> Telemetry { get; } =
            new();
    }
}