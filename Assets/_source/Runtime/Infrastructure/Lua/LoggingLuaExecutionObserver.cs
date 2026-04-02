using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Infrastructure.Lua
{
    public sealed class LoggingLuaExecutionObserver : ILuaExecutionObserver
    {
        private readonly IGameLogger _logger;

        public LoggingLuaExecutionObserver(IGameLogger logger)
        {
            _logger = logger;
        }

        public void OnLuaSuccess(string resultSummary)
        {
            _logger.LogInfo(GameLogFeature.MessagePipe, $"Lua execution OK: {resultSummary}");
        }

        public void OnLuaFailure(string errorMessage)
        {
            _logger.LogWarning(GameLogFeature.MessagePipe, $"Lua execution failed: {errorMessage}");
        }

        public void OnLuaRepairScheduled(int nextGeneration, string errorPreview)
        {
            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"Scheduling Programmer Lua repair, generation={nextGeneration}: {errorPreview}");
        }
    }
}
