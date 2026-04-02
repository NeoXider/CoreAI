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
        private readonly GameSessionSnapshot _snapshot = new GameSessionSnapshot();

        public void SetTelemetry(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;
            _snapshot.Telemetry[key.Trim()] = value ?? "";
        }

        public void SetTelemetry(string key, int value) => SetTelemetry(key, value.ToString());
        public void SetTelemetry(string key, float value) => SetTelemetry(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public void SetTelemetry(string key, bool value) => SetTelemetry(key, value ? "true" : "false");

        public GameSessionSnapshot BuildSnapshot()
        {
            var copy = new GameSessionSnapshot();
            foreach (var kv in _snapshot.Telemetry)
                copy.Telemetry[kv.Key] = kv.Value;
            return copy;
        }
    }
}
