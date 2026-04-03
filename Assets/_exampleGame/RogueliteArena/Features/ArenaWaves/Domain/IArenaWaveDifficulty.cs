namespace CoreAI.ExampleGame.ArenaWaves.Domain
{
    /// <summary>Нелинейная кривая сложности волн (суммарный рост + локальные провалы/пики).</summary>
    public interface IArenaWaveDifficulty
    {
        /// <param name="waveIndex1Based">Текущая волна, с 1.</param>
        /// <param name="totalWavesInRun">Всего волн до победы.</param>
        ArenaWaveDifficultySample Evaluate(int waveIndex1Based, int totalWavesInRun);
    }
}
