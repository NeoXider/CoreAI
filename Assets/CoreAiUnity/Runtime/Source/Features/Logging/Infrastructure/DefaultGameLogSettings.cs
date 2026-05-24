namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Default game log filtering settings used when no asset is provided.
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
