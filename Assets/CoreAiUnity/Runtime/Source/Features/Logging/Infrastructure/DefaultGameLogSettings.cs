namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Default game log filtering settings used when no asset is provided: every category enabled,
    /// <see cref="GameLogLevel.Info"/> and above.
    /// </summary>
    public sealed class DefaultGameLogSettings : IGameLogSettings
    {
        /// <inheritdoc />
        public bool ShouldLog(GameLogFeature feature, GameLogLevel level)
        {
            if (feature == GameLogFeature.None)
            {
                return false;
            }

            return (GameLogDefaults.EnabledFeatures & feature) != 0 && level >= GameLogDefaults.MinimumLevel;
        }
    }
}
