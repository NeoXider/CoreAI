namespace CoreAI.ExampleGame.ArenaWaves.Domain
{
    /// <summary>Nonlinear wave difficulty curve with overall growth and local dips or spikes.</summary>
    public interface IArenaWaveDifficulty
    {
        /// <param name="waveIndex1Based">Current one-based wave index.</param>
        /// <param name="totalWavesInRun">Total number of waves before victory.</param>
        ArenaWaveDifficultySample Evaluate(int waveIndex1Based, int totalWavesInRun);
    }
}
