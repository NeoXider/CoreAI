using System;
using System.Collections.Generic;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Instances;
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
        private readonly LuaCsRbxApiBindings _roblox;
        private readonly LuaCsRbxHttpServiceAdapter _rbxHttp;
        private readonly bool _registerWorldEditBuildBindings;
        private readonly LuaCsLogicSlots _logicSlots;

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
            LuaCapabilities capabilities = LuaCapabilities.All,
            LuaCsRbxApiBindings rbxApi = null,
            bool registerWorldEditBuildBindings = true,
            IRbxHttpRequestPolicy rbxHttpPolicy = null,
            IRbxHttpTransport rbxHttpTransport = null,
            IRbxHttpDestinationResolver rbxHttpResolver = null,
            int rbxHttpRequestsPerWindow = LuaCsRbxHttpServiceAdapter.DefaultRequestsPerWindow,
            double rbxHttpRateWindowSeconds = LuaCsRbxHttpServiceAdapter.DefaultRateWindowSeconds,
            Func<double> rbxMonotonicClock = null)
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
                new LuaCsDefaultRuntimeBindings(),
                rbxApi,
                registerWorldEditBuildBindings,
                rbxHttpPolicy,
                rbxHttpTransport,
                rbxHttpResolver,
                rbxHttpRequestsPerWindow,
                rbxHttpRateWindowSeconds,
                rbxMonotonicClock)
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
            LuaCsDefaultRuntimeBindings defaultBindings = null,
            LuaCsRbxApiBindings rbxApi = null,
            bool registerWorldEditBuildBindings = true,
            IRbxHttpRequestPolicy rbxHttpPolicy = null,
            IRbxHttpTransport rbxHttpTransport = null,
            IRbxHttpDestinationResolver rbxHttpResolver = null,
            int rbxHttpRequestsPerWindow = LuaCsRbxHttpServiceAdapter.DefaultRequestsPerWindow,
            double rbxHttpRateWindowSeconds = LuaCsRbxHttpServiceAdapter.DefaultRateWindowSeconds,
            Func<double> rbxMonotonicClock = null)
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
            _roblox = rbxApi;
            _logicSlots = new LuaCsLogicSlots(
                stateResolver: state => ResolveLogicOwnerState(state));
            _rbxHttp = rbxApi == null
                ? null
                : new LuaCsRbxHttpServiceAdapter(
                    rbxApi, rbxHttpPolicy, rbxHttpTransport,
                    rbxHttpResolver,
                    rbxHttpRequestsPerWindow, rbxHttpRateWindowSeconds,
                    rbxMonotonicClock);
            _registerWorldEditBuildBindings = registerWorldEditBuildBindings;
        }

        private IScriptState ResolveLogicOwnerState(IScriptState fallbackState)
        {
            if (_roblox == null)
            {
                return fallbackState;
            }

            Lua.LuaState fallback = CoreAI.Scripting.LuaCs.LuaCsScriptState.Unwrap(fallbackState);
            Lua.LuaState owner = _roblox.ResolveSchedulerOwnerState(fallback);
            return ReferenceEquals(fallback, owner)
                ? fallbackState
                : new CoreAI.Scripting.LuaCs.LuaCsScriptState(owner);
        }

        /// <summary>Optional Roblox API surface shared by every mod and the one-off executor.</summary>
        public LuaCsRbxApiBindings RbxApi => _roblox;

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
            RegisterCore(registry, capabilities, ownerModId, null, null);
        }

        /// <summary>Registers the one-off Rbx surface for a trusted actor mutation envelope.</summary>
        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities,
            string ownerModId, ActorContext actorContext, MutationEnvelope mutationEnvelope)
        {
            RegisterCore(registry, capabilities, ownerModId, actorContext, mutationEnvelope);
        }

        private void RegisterCore(IScriptFunctionRegistry registry, LuaCapabilities capabilities,
            string ownerModId, ActorContext? actorContext, MutationEnvelope? mutationEnvelope)
        {
            LuaCapabilities effective = _capabilities & capabilities;

            _default.Register(registry, effective);

            if ((effective & LuaCapabilities.Read) != 0)
            {
                _logging.RegisterGameplayApis(registry);
                _versioning?.RegisterGameplayApis(registry);
                _worldQuery?.RegisterGameplayApis(registry);
            }

            // WHY: the Roblox surface trims itself (Read gate inside, Instance.new under WorldEdit)
            // and threads the owner mod id so created instances land in the ownership ledger.
            if (_roblox != null)
            {
                if (actorContext.HasValue && mutationEnvelope.HasValue)
                {
                    _roblox.Register(registry, effective, ownerModId,
                        actorContext.Value, mutationEnvelope.Value);
                }
                else
                {
                    _roblox.Register(registry, effective, ownerModId);
                }
            }
            _rbxHttp?.Register(registry);

            // WHY: a composition may keep the WorldEdit capability (the Rbx surface needs it for
            // Instance.new) while withholding the low-level coreai_world_*/component build APIs.
            if ((effective & LuaCapabilities.WorldEdit) != 0)
            {
                if (_registerWorldEditBuildBindings)
                {
                    _world?.RegisterGameplayApis(registry);
                    _components?.RegisterGameplayApis(registry);
                }
                else
                {
                    // WHY: a bare "attempt to call a nil value" feeds the mod's error streak with no
                    // hint the cause is composition; the stubs answer with an actionable error that
                    // names the withheld surface and points at the Rbx alternative.
                    RegisterWithheldStubs(registry, LuaCsWorldRuntimeBindings.BuildApiNames, WorldBuildStubError);
                    RegisterWithheldStubs(registry, LuaCsComponentRuntimeBindings.BuildApiNames, WorldBuildStubError);
                }
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

            if ((effective & LuaCapabilities.Full) != 0 && _full != null)
            {
                _full.RegisterGameplayApis(registry);
            }
            else
            {
                // WHY: same actionable-stub treatment for the opt-in Full tier — a mod declaring
                // "capabilities: Full" under a host ceiling without Full, OR a composition that grants
                // Full but never wired the Full surface (_full == null), would otherwise quarantine
                // itself on nil-call errors that never mention the missing grant/wiring.
                RegisterWithheldStubs(registry, LuaCsFullUnityRuntimeBindings.ApiNames, FullStubError);
            }
        }

        /// <summary>
        /// Registers a throwing stub for every withheld API name so a call raises a typed,
        /// actionable <see cref="LuaApiWithheldException"/> instead of "attempt to call a nil value".
        /// </summary>
        private static void RegisterWithheldStubs(
            IScriptFunctionRegistry registry,
            IReadOnlyList<string> apiNames,
            Func<string, Exception> errorFactory)
        {
            for (int i = 0; i < apiNames.Count; i++)
            {
                string name = apiNames[i];
                registry.RegisterVarArgs(name, _ => throw errorFactory(name));
            }
        }

        private static Exception WorldBuildStubError(string apiName)
        {
            return new LuaApiWithheldException(apiName, LuaCapabilities.WorldEdit,
                apiName + " requires the WorldEdit build bindings, which are disabled for this mod; " +
                "use the Rbx API instead (e.g. Instance.new('Part') to spawn, instance:Destroy() to remove).");
        }

        private static Exception FullStubError(string apiName)
        {
            return new LuaApiWithheldException(apiName, LuaCapabilities.Full,
                apiName + " requires the Full capability, which was not granted to this mod; " +
                "grant Full to this mod (host opt-in) to use the unity_* reflection APIs.");
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
