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
    }
}
