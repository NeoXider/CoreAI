namespace CoreAI.Infrastructure.Logging
{
    public sealed class GameLogSettingsOptions : IGameLogSettings
    {
        public GameLogFeature EnabledFeatures { get; set; } = GameLogFeature.AllBuiltIn;
        public GameLogLevel MinimumLevel { get; set; } = GameLogLevel.Debug;

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