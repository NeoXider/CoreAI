#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Sandbox;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Aggregating Game Lua Runtime Bindings component used by CoreAI. Registration is gated by
    /// <see cref="LuaCapabilities"/>: a binding group is only exposed when its tier is granted,
    /// so a restricted script physically lacks the disallowed functions (default: all tiers,
    /// preserving historical behavior).
    /// </summary>
    public sealed class AggregatingGameLuaRuntimeBindings : IGameLuaRuntimeBindings, ICapabilityScopedLuaBindings
    {
        private readonly IGameLogger _logger;
        private readonly CoreAiVersioningLuaRuntimeBindings _versioning;
        private readonly World.CoreAiWorldLuaRuntimeBindings _world;
        private readonly LuaTimeBindings _time;
        private readonly World.CoreAiWorldQueryLuaBindings _worldQuery;
        private readonly LuaLogicSlots _logicSlots;
        private readonly CoreAiFullUnityLuaRuntimeBindings _full;
        private readonly LuaCapabilities _capabilities;

        public AggregatingGameLuaRuntimeBindings(
            IGameLogger logger,
            CoreAiVersioningLuaRuntimeBindings versioning,
            World.CoreAiWorldLuaRuntimeBindings world,
            LuaTimeBindings time = null,
            World.CoreAiWorldQueryLuaBindings worldQuery = null,
            LuaLogicSlots logicSlots = null,
            CoreAiFullUnityLuaRuntimeBindings full = null,
            LuaCapabilities capabilities = LuaCapabilities.All)
        {
            _logger = logger;
            _versioning = versioning;
            _world = world;
            _time = time ?? new LuaTimeBindings();
            _worldQuery = worldQuery;
            _logicSlots = logicSlots;
            _full = full;
            _capabilities = capabilities;
        }

        /// <summary>Capability tiers granted to scripts created from this aggregator.</summary>
        public LuaCapabilities Capabilities => _capabilities;

        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            RegisterGameplayApis(registry, _capabilities);
        }

        /// <summary>
        /// Registers only the binding groups allowed by the intersection of this aggregator's
        /// tier and the requested tier (a consumer can narrow, never widen, the granted surface).
        /// </summary>
        public void RegisterGameplayApis(LuaApiRegistry registry, LuaCapabilities capabilities)
        {
            LuaCapabilities effective = _capabilities & capabilities;

            if ((effective & LuaCapabilities.Read) != 0)
            {
                new LoggingLuaRuntimeBindings(_logger).RegisterGameplayApis(registry);
                _versioning.RegisterGameplayApis(registry);
                _worldQuery?.RegisterGameplayApis(registry);
            }

            if ((effective & LuaCapabilities.WorldEdit) != 0)
            {
                _world?.RegisterGameplayApis(registry);
            }

            if ((effective & LuaCapabilities.Gameplay) != 0)
            {
                _time.RegisterTimeApis(registry);
            }

            if ((effective & LuaCapabilities.LogicOverride) != 0)
            {
                _logicSlots?.RegisterApis(registry);
            }

            if ((effective & LuaCapabilities.Full) != 0)
            {
                _full?.RegisterGameplayApis(registry);
            }

            GameLuaBindingsExtensibility.RegisterAll(registry, effective);
        }
    }
}
#endif