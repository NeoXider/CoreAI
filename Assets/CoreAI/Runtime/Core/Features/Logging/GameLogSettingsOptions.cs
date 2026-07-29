namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Portable snapshot of log filtering rules, free of Unity types.
    /// </summary>
    public sealed class GameLogSettingsOptions : IGameLogSettings
    {
        /// <summary>Categories allowed through the filter.</summary>
        public GameLogFeature EnabledFeatures { get; set; } = GameLogFeature.All;

        /// <summary>Lowest level allowed through the filter.</summary>
        public GameLogLevel MinimumLevel { get; set; } = GameLogLevel.Debug;

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
