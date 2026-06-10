#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Sandbox;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Aggregating Game Lua Runtime Bindings component used by CoreAI.
    /// </summary>
    public sealed class AggregatingGameLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        private readonly IGameLogger _logger;
        private readonly CoreAiVersioningLuaRuntimeBindings _versioning;
        private readonly World.CoreAiWorldLuaRuntimeBindings _world;
        private readonly LuaTimeBindings _time;

        public AggregatingGameLuaRuntimeBindings(
            IGameLogger logger,
            CoreAiVersioningLuaRuntimeBindings versioning,
            World.CoreAiWorldLuaRuntimeBindings world,
            LuaTimeBindings time = null)
        {
            _logger = logger;
            _versioning = versioning;
            _world = world;
            _time = time ?? new LuaTimeBindings();
        }

        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            new LoggingLuaRuntimeBindings(_logger).RegisterGameplayApis(registry);
            _versioning.RegisterGameplayApis(registry);
            _world?.RegisterGameplayApis(registry);
            _time.RegisterTimeApis(registry);
            GameLuaBindingsExtensibility.RegisterAll(registry);
        }
    }
}
#endif