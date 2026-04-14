namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Всё включено (удобно до появления своего <see cref="GameLogSettingsAsset"/>).
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

            return level >= GameLogLevel.Warning;
        }
    }
}