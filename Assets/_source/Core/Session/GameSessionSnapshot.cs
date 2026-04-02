namespace CoreAI.Session
{
    /// <summary>
    /// Минимальный DTO снимка сессии для промпта и телеметрии (DGF_SPEC §2).
    /// </summary>
    public sealed class GameSessionSnapshot
    {
        public int WaveIndex { get; set; }
        public string ModeId { get; set; } = "default";
        public int PartySize { get; set; } = 1;

        /// <summary>Опционально: HP игрока (текущий). -1 если игра не обновляет.</summary>
        public int PlayerHpCurrent { get; set; } = -1;

        /// <summary>Опционально: HP игрока (максимум). -1 если игра не обновляет.</summary>
        public int PlayerHpMax { get; set; } = -1;

        /// <summary>Опционально: сколько врагов живо сейчас. -1 если игра не обновляет.</summary>
        public int AliveEnemies { get; set; } = -1;
    }
}
