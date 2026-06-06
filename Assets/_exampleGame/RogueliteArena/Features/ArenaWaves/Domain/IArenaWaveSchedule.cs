namespace CoreAI.ExampleGame.ArenaWaves.Domain
{
    /// <summary>Enemy-count rule per wave; can be replaced with validated AI-authored data.</summary>
    public interface IArenaWaveSchedule
    {
        /// <param name="waveIndex1Based">One-based wave number.</param>
        int GetEnemyCountForWave(int waveIndex1Based);
    }
}