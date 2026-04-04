using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Фильтрует по <see cref="IGameLogSettings"/>, затем пишет в <see cref="UnityGameLogSink"/>.
    /// </summary>
    public sealed class FilteringGameLogger : IGameLogger
    {
        private readonly UnityGameLogSink _sink;
        private readonly IGameLogSettings _settings;

        /// <summary>Связывает низкоуровневый sink с правилами фильтрации.</summary>
        public FilteringGameLogger(UnityGameLogSink sink, IGameLogSettings settings)
        {
            _sink = sink;
            _settings = settings;
        }

        /// <inheritdoc />
        public void LogDebug(GameLogFeature feature, string message, Object context = null)
        {
            if (!_settings.ShouldLog(feature, GameLogLevel.Debug))
            {
                return;
            }

            _sink.Write(GameLogLevel.Debug, Format(feature, message), context);
        }

        /// <inheritdoc />
        public void LogInfo(GameLogFeature feature, string message, Object context = null)
        {
            if (!_settings.ShouldLog(feature, GameLogLevel.Info))
            {
                return;
            }

            _sink.Write(GameLogLevel.Info, Format(feature, message), context);
        }

        /// <inheritdoc />
        public void LogWarning(GameLogFeature feature, string message, Object context = null)
        {
            if (!_settings.ShouldLog(feature, GameLogLevel.Warning))
            {
                return;
            }

            _sink.Write(GameLogLevel.Warning, Format(feature, message), context);
        }

        /// <inheritdoc />
        public void LogError(GameLogFeature feature, string message, Object context = null)
        {
            if (!_settings.ShouldLog(feature, GameLogLevel.Error))
            {
                return;
            }

            _sink.Write(GameLogLevel.Error, Format(feature, message), context);
        }

        private static string Format(GameLogFeature feature, string message)
        {
            return $"[{feature}] {message}";
        }
    }
}