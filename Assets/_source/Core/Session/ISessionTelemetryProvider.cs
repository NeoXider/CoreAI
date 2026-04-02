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

        public void SetPlayerHp(int current, int max)
        {
            _snapshot.PlayerHpCurrent = current;
            _snapshot.PlayerHpMax = max;
        }

        public void SetAliveEnemies(int aliveEnemies)
        {
            _snapshot.AliveEnemies = aliveEnemies;
        }

        public GameSessionSnapshot BuildSnapshot() => new GameSessionSnapshot
        {
            WaveIndex = _snapshot.WaveIndex,
            ModeId = _snapshot.ModeId,
            PartySize = _snapshot.PartySize,
            PlayerHpCurrent = _snapshot.PlayerHpCurrent,
            PlayerHpMax = _snapshot.PlayerHpMax,
            AliveEnemies = _snapshot.AliveEnemies
        };
    }
}
