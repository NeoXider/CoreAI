using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Capability-scoped Lua-CSharp gameplay binding aggregator for <see cref="LuaCsModRuntime"/>.
    /// </summary>
    public sealed class LuaCsGameplayBindings : ILuaTransactionScope
    {
        private readonly LuaCapabilities _capabilities;
        private readonly LuaCsDefaultRuntimeBindings _default;
        private readonly LuaCsLoggingRuntimeBindings _logging;
        private readonly LuaCsVersioningRuntimeBindings _versioning;
        private readonly LuaCsWorldRuntimeBindings _world;
        private readonly LuaCsComponentRuntimeBindings _components;
        private readonly LuaCsTimeBindings _time;
        private readonly LuaCsWorldQueryBindings _worldQuery;
        private readonly LuaCsFullUnityRuntimeBindings _full;
        private readonly LuaCsInputRuntimeBindings _input;
        private readonly LuaCsLogicSlots _logicSlots = new();

        public LuaCsGameplayBindings(
            IGameLogger logger,
            ILuaScriptVersionStore luaScriptVersions,
            IDataOverlayVersionStore dataOverlayVersions,
            IAiGameCommandSink commandSink,
            ICoreAiPrefabRegistry prefabRegistry = null,
            IEnumerable<string> allowedScenes = null,
            IDataOverlayPayloadValidator validator = null,
            IFullLuaAccessBlacklistPolicy fullBlacklistPolicy = null,
            bool allowNonPublicFullMembers = false,
            LuaCapabilities capabilities = LuaCapabilities.All)
            : this(
                logger,
                new LuaCsVersioningRuntimeBindings(
                    luaScriptVersions,
                    dataOverlayVersions,
                    commandSink,
                    validator),
                new LuaCsWorldRuntimeBindings(commandSink, allowedScenes),
                new LuaCsComponentRuntimeBindings(commandSink),
                new LuaCsTimeBindings(),
                new LuaCsWorldQueryBindings(prefabRegistry),
                new LuaCsFullUnityRuntimeBindings(logger, allowNonPublicFullMembers, fullBlacklistPolicy),
                capabilities,
                new LuaCsInputRuntimeBindings(),
                new LuaCsDefaultRuntimeBindings())
        {
        }

        public LuaCsGameplayBindings(
            IGameLogger logger,
            LuaCsVersioningRuntimeBindings versioning,
            LuaCsWorldRuntimeBindings world,
            LuaCsComponentRuntimeBindings components = null,
            LuaCsTimeBindings time = null,
            LuaCsWorldQueryBindings worldQuery = null,
            LuaCsFullUnityRuntimeBindings full = null,
            LuaCapabilities capabilities = LuaCapabilities.All,
            LuaCsInputRuntimeBindings input = null,
            LuaCsDefaultRuntimeBindings defaultBindings = null)
        {
            _capabilities = capabilities;
            _default = defaultBindings ?? new LuaCsDefaultRuntimeBindings();
            _logging = new LuaCsLoggingRuntimeBindings(logger);
            _versioning = versioning;
            _world = world;
            _components = components;
            _time = time ?? new LuaCsTimeBindings();
            _worldQuery = worldQuery;
            _full = full;
            _input = input;
        }

        public LuaCapabilities Capabilities => _capabilities;

        /// <summary>
        /// Shared logic-override slots (<c>logic_define</c>/<c>logic_reset</c>/<c>logic_list</c>). The host
        /// declares slots and reads them via <see cref="LuaCsLogicSlots.TryInvokeNumber"/>; mods override
        /// them. Both the persistent runtime and the one-off executor register through this one instance,
        /// so a host resolving it sees every mod's overrides.
        /// </summary>
        public LuaCsLogicSlots LogicSlots => _logicSlots;

        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities)
        {
            Register(registry, capabilities, null);
        }

        /// <summary>
        /// Registers the capability-scoped surface on one script registry. <paramref name="ownerModId"/>
        /// identifies the persistent mod that owns the registry (null for ownerless surfaces such as the
        /// one-off executor) so ownership-tracked APIs — the logic slots — can attribute and later tear
        /// down what that mod registered.
        /// </summary>
        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities, string ownerModId)
        {
            LuaCapabilities effective = _capabilities & capabilities;

            _default.Register(registry, effective);

            if ((effective & LuaCapabilities.Read) != 0)
            {
                _logging.RegisterGameplayApis(registry);
                _versioning?.RegisterGameplayApis(registry);
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
                _logicSlots.RegisterApis(registry, ownerModId);
            }

            if ((effective & LuaCapabilities.Full) != 0)
            {
                _full?.RegisterGameplayApis(registry);
            }
        }

        public void ResetTransactions()
        {
            (_world as ILuaTransactionScope)?.ResetTransactions();
        }

        /// <inheritdoc />
        public void PushTransactionScope()
        {
            (_world as ILuaTransactionScope)?.PushTransactionScope();
        }

        /// <inheritdoc />
        public void PopTransactionScope()
        {
            (_world as ILuaTransactionScope)?.PopTransactionScope();
        }
    }
}
