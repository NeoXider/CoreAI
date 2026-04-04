namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Правила: какие <see cref="GameLogFeature"/> и с какого <see cref="GameLogLevel"/> писать в вывод.
    /// </summary>
    public interface IGameLogSettings
    {
        /// <summary>Разрешён ли вывод для пары категория + уровень.</summary>
        bool ShouldLog(GameLogFeature feature, GameLogLevel level);
    }
}