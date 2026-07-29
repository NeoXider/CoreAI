using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Game logger that filters entries before writing them to a sink.
    /// </summary>
    public sealed class FilteringGameLogger : IGameLogger
    {
        private readonly IGameLogSink _sink;
        private readonly IGameLogSettings _settings;

        /// <summary>Initializes a new instance of FilteringGameLogger.</summary>
        public FilteringGameLogger(IGameLogSink sink, IGameLogSettings settings)
        {
            _sink = sink;

            // WHY: A logger without settings would throw on every call; falling back to the live
            // runtime filter keeps an unconfigured logger filtering like every other one.
            _settings = settings ?? GameLogFilter.Settings;
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
