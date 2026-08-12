namespace CoreAI.Infrastructure.Logging
{
    public interface IGameLogSettings
    {
        bool ShouldLog(GameLogFeature feature, GameLogLevel level);
    }

    /// <summary>
    /// Optional log formatting settings. Loggers preserve both legacy prefixes when their
    /// <see cref="IGameLogSettings"/> implementation does not implement this interface.
    /// </summary>
    public interface IGameLogFormattingSettings
    {
        /// <summary>Whether Unity output starts with the library-level [CoreAI] prefix.</summary>
        bool IncludeCoreAiPrefix { get; }

        /// <summary>Whether messages passed to a sink start with their feature prefix.</summary>
        bool IncludeFeaturePrefix { get; }
    }
}
