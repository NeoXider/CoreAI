namespace CoreAI.Infrastructure.Logging
{
    public interface IGameLogSettings
    {
        bool ShouldLog(GameLogFeature feature, GameLogLevel level);
    }
}
