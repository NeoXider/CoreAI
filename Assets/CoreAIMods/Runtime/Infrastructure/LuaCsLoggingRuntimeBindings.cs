using System;
using CoreAI.Infrastructure.Logging;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua-CSharp counterpart of <see cref="CoreAI.Infrastructure.Lua.LoggingLuaRuntimeBindings"/>.
    /// </summary>
    public sealed class LuaCsLoggingRuntimeBindings
    {
        private readonly IGameLogger _logger;

        public LuaCsLoggingRuntimeBindings(IGameLogger logger)
        {
            _logger = logger;
        }

        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities)
        {
            if ((capabilities & LuaCapabilities.Read) == 0)
            {
                return;
            }

            RegisterGameplayApis(registry);
        }

        public void RegisterGameplayApis(IScriptFunctionRegistry registry)
        {
            registry.Register("report", (Action<string>)(msg =>
                _logger.LogInfo(GameLogFeature.MessagePipe, $"[Lua report] {msg}")));
            registry.Register("add", new Func<double, double, double>((a, b) => a + b));
        }
    }
}
