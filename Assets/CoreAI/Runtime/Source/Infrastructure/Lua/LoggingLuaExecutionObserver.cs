using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>Пишет события выполнения Lua (<see cref="ILuaExecutionObserver"/>) в игровой лог.</summary>
    public sealed class LoggingLuaExecutionObserver : ILuaExecutionObserver
    {
        private readonly IGameLogger _logger;

        /// <param name="logger">Куда писать строки о успехе, ошибке и плане починки.</param>
        public LoggingLuaExecutionObserver(IGameLogger logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public void OnLuaSuccess(string resultSummary)
        {
            _logger.LogInfo(GameLogFeature.MessagePipe, $"Lua execution OK: {resultSummary}");
        }

        /// <inheritdoc />
        public void OnLuaFailure(string errorMessage)
        {
            _logger.LogWarning(GameLogFeature.MessagePipe, $"Lua execution failed: {errorMessage}");
        }

        /// <inheritdoc />
        public void OnLuaRepairScheduled(int nextGeneration, string errorPreview)
        {
            _logger.LogInfo(GameLogFeature.MessagePipe,
                $"Scheduling Programmer Lua repair, generation={nextGeneration}: {errorPreview}");
        }
    }
}
