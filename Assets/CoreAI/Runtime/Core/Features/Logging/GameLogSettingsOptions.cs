namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Portable snapshot of log filtering rules, free of Unity types.
    /// </summary>
    public sealed class GameLogSettingsOptions : IGameLogSettings, IGameLogFormattingSettings
    {
        /// <summary>Categories allowed through the filter.</summary>
        public GameLogFeature EnabledFeatures { get; set; } = GameLogFeature.All;

        /// <summary>Lowest level allowed through the filter.</summary>
        public GameLogLevel MinimumLevel { get; set; } = GameLogLevel.Debug;

        /// <inheritdoc />
        public bool IncludeCoreAiPrefix { get; set; } = true;

        /// <inheritdoc />
        public bool IncludeFeaturePrefix { get; set; } = true;

        /// <inheritdoc />
        public bool ShouldLog(GameLogFeature feature, GameLogLevel level)
        {
            if (feature == GameLogFeature.None)
            {
                return false;
            }

            return (EnabledFeatures & feature) != 0 && level >= MinimumLevel;
        }
    }
}
