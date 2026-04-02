namespace CoreAI.Session
{
    public sealed class GameSessionSnapshot
    {
        /// <summary>
        /// Игронезависимая телеметрия, которую игра сама обновляет в Core.
        /// Ключи и значения определяет тайтл (например: "wave", "player.hp.current", "player.style", ...).
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string> Telemetry { get; } =
            new System.Collections.Generic.Dictionary<string, string>();
    }
}
