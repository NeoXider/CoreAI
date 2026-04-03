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
        private readonly CoreAI.Infrastructure.World.CoreAiWorldLuaRuntimeBindings _world;

        public AggregatingGameLuaRuntimeBindings(
            IGameLogger logger,
            CoreAiVersioningLuaRuntimeBindings versioning,
            CoreAI.Infrastructure.World.CoreAiWorldLuaRuntimeBindings world)
        {
            _logger = logger;
            _versioning = versioning;
            _world = world;
        }

        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            new LoggingLuaRuntimeBindings(_logger).RegisterGameplayApis(registry);
            _versioning.RegisterGameplayApis(registry);
            _world?.RegisterGameplayApis(registry);
            GameLuaBindingsExtensibility.RegisterAll(registry);
        }
    }
}
