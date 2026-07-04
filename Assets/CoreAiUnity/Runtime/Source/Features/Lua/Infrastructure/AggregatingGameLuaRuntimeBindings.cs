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
    public sealed class AggregatingGameLuaRuntimeBindings
        : IGameLuaRuntimeBindings, ICapabilityScopedLuaBindings, ILuaTransactionScope
    {
        private readonly IGameLogger _logger;
        private readonly CoreAiVersioningLuaRuntimeBindings _versioning;
        private readonly World.CoreAiWorldLuaRuntimeBindings _world;
        private readonly World.CoreAiComponentLuaRuntimeBindings _components;
        private readonly LuaTimeBindings _time;
        private readonly World.CoreAiWorldQueryLuaBindings _worldQuery;
        private readonly LuaLogicSlots _logicSlots;
        private readonly CoreAiFullUnityLuaRuntimeBindings _full;
        private readonly CoreAiInputLuaRuntimeBindings _input;
        private readonly LuaCapabilities _capabilities;

        public AggregatingGameLuaRuntimeBindings(
            IGameLogger logger,
            CoreAiVersioningLuaRuntimeBindings versioning,
            World.CoreAiWorldLuaRuntimeBindings world,
            World.CoreAiComponentLuaRuntimeBindings components = null,
            LuaTimeBindings time = null,
            World.CoreAiWorldQueryLuaBindings worldQuery = null,
            LuaLogicSlots logicSlots = null,
            CoreAiFullUnityLuaRuntimeBindings full = null,
            LuaCapabilities capabilities = LuaCapabilities.All,
            CoreAiInputLuaRuntimeBindings input = null)
        {
            _logger = logger;
            _versioning = versioning;
            _world = world;
            _components = components;
            _time = time ?? new LuaTimeBindings();
            _worldQuery = worldQuery;
            _logicSlots = logicSlots;
            _full = full;
            _input = input;
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
                _components?.RegisterGameplayApis(registry);
            }

            if ((effective & LuaCapabilities.Gameplay) != 0)
            {
                _time.RegisterTimeApis(registry);
                _input?.RegisterGameplayApis(registry);
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

        /// <summary>
        /// Forwards a transaction reset to every wrapped binding set that owns mutable per-run
        /// transaction state (currently the world bindings). Lets top-level executors clear a
        /// transaction leaked by a previously aborted chunk through the single aggregator they hold.
        /// </summary>
        public void ResetTransactions()
        {
            (_world as ILuaTransactionScope)?.ResetTransactions();
        }
    }
}
#endif
