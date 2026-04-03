using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Sandbox;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Демо-API ядра + опциональные биндинги из <see cref="GameLuaBindingsExtensibility"/>.
    /// </summary>
    public sealed class AggregatingGameLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        private readonly IGameLogger _logger;
        private readonly CoreAiVersioningLuaRuntimeBindings _versioning;

        public AggregatingGameLuaRuntimeBindings(IGameLogger logger, CoreAiVersioningLuaRuntimeBindings versioning)
        {
            _logger = logger;
            _versioning = versioning;
        }

        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            new LoggingLuaRuntimeBindings(_logger).RegisterGameplayApis(registry);
            _versioning.RegisterGameplayApis(registry);
            GameLuaBindingsExtensibility.RegisterAll(registry);
        }
    }
}
