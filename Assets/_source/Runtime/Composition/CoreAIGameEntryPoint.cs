using CoreAI.Infrastructure.Logging;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Точка старта после построения контейнера (аналог раннего bootstrap без MonoBehaviour).
    /// </summary>
    public sealed class CoreAIGameEntryPoint : IStartable
    {
        private readonly IGameLogger _logger;

        public CoreAIGameEntryPoint(IGameLogger logger)
        {
            _logger = logger;
        }

        public void Start()
        {
            _logger.LogInfo(GameLogFeature.Composition,
                "VContainer + MessagePipe (GlobalMessagePipe) + IGameLogger с фильтром по фичам готовы.");
        }
    }
}
