using System;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Sandbox;

namespace CoreAI.Infrastructure.Lua
{
    public sealed class LoggingLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        private readonly IGameLogger _logger;

        public LoggingLuaRuntimeBindings(IGameLogger logger)
        {
            _logger = logger;
        }

        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("report", (Action<string>)(msg =>
                _logger.LogInfo(GameLogFeature.MessagePipe, $"[Lua report] {msg}")));
            registry.Register("add", new Func<double, double, double>((a, b) => a + b));
        }
    }
}
