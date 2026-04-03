using System;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Sandbox;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Регистрирует демо-API для песочницы Lua: <c>report</c> (лог) и <c>add</c> (сложение).
    /// </summary>
    public sealed class LoggingLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        private readonly IGameLogger _logger;

        /// <param name="logger">Приёмник для вызовов <c>report</c> из скриптов.</param>
        public LoggingLuaRuntimeBindings(IGameLogger logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("report", (Action<string>)(msg =>
                _logger.LogInfo(GameLogFeature.MessagePipe, $"[Lua report] {msg}")));
            registry.Register("add", new Func<double, double, double>((a, b) => a + b));
        }
    }
}
