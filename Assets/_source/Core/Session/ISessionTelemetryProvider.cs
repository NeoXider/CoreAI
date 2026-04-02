namespace CoreAI.Session
{
    public interface ISessionTelemetryProvider
    {
        GameSessionSnapshot BuildSnapshot();
    }

    /// <summary>
    /// MVP-сборщик: позже подключается к игровым сервисам.
    /// </summary>
    public sealed class SessionTelemetryCollector : ISessionTelemetryProvider
    {
        private GameSessionSnapshot _snapshot = new GameSessionSnapshot();

        public void SetWave(int wave) => _snapshot.WaveIndex = wave;

        public GameSessionSnapshot BuildSnapshot() => new GameSessionSnapshot
        {
            WaveIndex = _snapshot.WaveIndex,
            ModeId = _snapshot.ModeId,
            PartySize = _snapshot.PartySize
        };
    }
}
