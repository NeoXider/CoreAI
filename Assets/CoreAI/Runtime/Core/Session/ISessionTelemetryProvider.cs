using System.Collections.Generic;

namespace CoreAI.Session
{
    /// <summary>ISessionTelemetryProvider interface.</summary>
    public interface ISessionTelemetryProvider
    {
        /// <summary>Builds a snapshot of the current session telemetry for prompt composition.</summary>
        GameSessionSnapshot BuildSnapshot();
    }

    /// <summary>
    /// Collects lightweight session telemetry values for prompt composition.
    /// </summary>
    public sealed class SessionTelemetryCollector : ISessionTelemetryProvider
    {
        private readonly GameSessionSnapshot _snapshot = new();

        /// <summary>Sets a session telemetry value using a strongly typed overload.</summary>
        public void SetTelemetry(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            _snapshot.Telemetry[key.Trim()] = value ?? "";
        }

        /// <summary>Sets a session telemetry value using a strongly typed overload.</summary>
        public void SetTelemetry(string key, int value)
        {
            SetTelemetry(key, value.ToString());
        }

        /// <summary>Sets a session telemetry value using a strongly typed overload.</summary>
        public void SetTelemetry(string key, float value)
        {
            SetTelemetry(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Sets a session telemetry value using a strongly typed overload.</summary>
        public void SetTelemetry(string key, bool value)
        {
            SetTelemetry(key, value ? "true" : "false");
        }

        /// <inheritdoc />
        public GameSessionSnapshot BuildSnapshot()
        {
            GameSessionSnapshot copy = new();
            foreach (KeyValuePair<string, string> kv in _snapshot.Telemetry)
            {
                copy.Telemetry[kv.Key] = kv.Value;
            }

            return copy;
        }
    }
}
