using System.Collections.Generic;

namespace CoreAI.Session
{
    /// <summary>Источник снимка сессии для <see cref="CoreAI.Ai.AiPromptComposer"/>.</summary>
    public interface ISessionTelemetryProvider
    {
        /// <summary>Непустой снимок с актуальной телеметрией для user-prompt.</summary>
        GameSessionSnapshot BuildSnapshot();
    }

    /// <summary>
    /// MVP-сборщик: позже подключается к игровым сервисам.
    /// </summary>
    public sealed class SessionTelemetryCollector : ISessionTelemetryProvider
    {
        private readonly GameSessionSnapshot _snapshot = new();

        /// <summary>Записать строковое значение телеметрии (перезапись по ключу).</summary>
        public void SetTelemetry(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            _snapshot.Telemetry[key.Trim()] = value ?? "";
        }

        /// <summary>Перегрузка для целых (сериализация в строку).</summary>
        public void SetTelemetry(string key, int value)
        {
            SetTelemetry(key, value.ToString());
        }

        /// <summary>Перегрузка для чисел с плавающей точкой (invariant culture).</summary>
        public void SetTelemetry(string key, float value)
        {
            SetTelemetry(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Перегрузка для логических значений (<c>true</c>/<c>false</c>).</summary>
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