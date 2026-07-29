namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Filtering rules applied when the game runs without an authored log settings asset.
    /// </summary>
    public static class GameLogDefaults
    {
        /// <summary>Categories enabled when nothing is authored: every category.</summary>
        public const GameLogFeature EnabledFeatures = GameLogFeature.All;

        /// <summary>Minimum level when nothing is authored: informational and above.</summary>
        public const GameLogLevel MinimumLevel = GameLogLevel.Info;
    }
}
