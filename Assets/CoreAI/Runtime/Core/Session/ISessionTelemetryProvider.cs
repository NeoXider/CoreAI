using System.Collections.Generic;

namespace CoreAI.Session
{
    /// <summary>Builds immutable snapshots of host session telemetry for prompt composition.</summary>
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

        /// <summary>Stores a string telemetry value after normalizing the key.</summary>
        public void SetTelemetry(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            _snapshot.Telemetry[key.Trim()] = value ?? "";
        }

        /// <summary>Stores an integer telemetry value using invariant string formatting.</summary>
        public void SetTelemetry(string key, int value)
        {
            SetTelemetry(key, value.ToString());
        }

        /// <summary>Stores a floating-point telemetry value using invariant string formatting.</summary>
        public void SetTelemetry(string key, float value)
        {
            SetTelemetry(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Stores a boolean telemetry value as <c>true</c> or <c>false</c>.</summary>
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