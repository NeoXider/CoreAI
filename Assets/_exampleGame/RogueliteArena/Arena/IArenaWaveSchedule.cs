namespace CoreAI.ExampleGame.Arena
{
    /// <summary>Правило «сколько врагов на волне» — можно заменить данными от ИИ после валидации.</summary>
    public interface IArenaWaveSchedule
    {
        /// <param name="waveIndex1Based">Номер волны, начиная с 1.</param>
        int GetEnemyCountForWave(int waveIndex1Based);
    }
}
