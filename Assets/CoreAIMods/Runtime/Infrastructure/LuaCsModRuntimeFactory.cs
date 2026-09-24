using System;
using System.Collections.Generic;
using CoreAI.Ai.Logging;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Host-service inputs for <see cref="LuaCsModRuntimeFactory.Create"/>. Mirrors the dependency set a
    /// future <c>CoreAiModsLifetimeScope</c> would resolve so the scope can populate this object from its
    /// container and hand it to the factory verbatim. Every service is optional at this layer: the
    /// sub-bindings and the runtime tolerate nulls (fail-closed / null-object), which keeps the factory
    /// usable in EditMode fixtures that only supply the services a given test needs.
    /// </summary>
    public sealed class LuaCsModStackOptions
    {
        // ---- Gameplay-binding host services (fed into LuaCsGameplayBindings) --------------------

        /// <summary>Unity-facing logger the ported gameplay bindings write through.</summary>
        public IGameLogger Logger;

        /// <summary>Backs the <c>coreai_lua_*</c> version/revert APIs (null =&gt; NullLuaScriptVersionStore).</summary>
        public ILuaScriptVersionStore LuaScriptVersions;

        /// <summary>Backs the <c>coreai_data_*</c> overlay APIs (null =&gt; NullDataOverlayVersionStore).</summary>
        public IDataOverlayVersionStore DataOverlayVersions;

        /// <summary>Receives world/data commands produced by WorldEdit-tier APIs.</summary>
        public IAiGameCommandSink CommandSink;

        /// <summary>Optional prefab lookup for read-tier world queries.</summary>
        public ICoreAiPrefabRegistry PrefabRegistry;

        /// <summary>Optional scene allow-list enforced by <c>coreai_world_load_scene</c>.</summary>
        public IEnumerable<string> AllowedScenes;

        /// <summary>Optional Full-tier reflection allow/deny policy (null =&gt; allow-all).</summary>
        public IFullLuaAccessBlacklistPolicy FullBlacklistPolicy;

        /// <summary>When true, Full-tier reflection may touch non-public members.</summary>
        public bool AllowNonPublicFullMembers;

        /// <summary>
        /// Optional Roblox API surface (roadmap §5.1.3: datatype constructors, Enum, Instance.new,
        /// game/workspace). One shared instance means every mod and the one-off executor operate on
        /// the same instance world. The runtime unloads the mods of every actor this surface
        /// disconnects (<see cref="LuaCsRbxApiBindings.DisconnectActor"/>), leaving their stored
        /// packages as they were, and <see cref="LogService"/>, when set, becomes the log a mod's
        /// <c>warn</c> writes to. Null = the Roblox globals are not installed.
        /// </summary>
        public LuaCsRbxApiBindings RbxApi;

        /// <summary>
        /// Host-owned outbound authorization policy. Null keeps mod HTTP disabled by default.
        /// </summary>
        public IRbxHttpRequestPolicy RbxHttpPolicy;

        /// <summary>
        /// Host-owned outbound transport. Null keeps the production transport refusing loudly even
        /// when a host policy is configured.
        /// </summary>
        public IRbxHttpTransport RbxHttpTransport;

        /// <summary>
        /// Host-owned DNS resolver. Null keeps domain resolution refusing even when policy and
        /// transport are configured.
        /// </summary>
        public IRbxHttpDestinationResolver RbxHttpResolver;

        /// <summary>Maximum outbound requests accepted per actor in one rate window.</summary>
        public int RbxHttpRequestsPerWindow =
            LuaCsRbxHttpServiceAdapter.DefaultRequestsPerWindow;

        /// <summary>Length of the per-actor outbound request rate window in seconds.</summary>
        public double RbxHttpRateWindowSeconds =
            LuaCsRbxHttpServiceAdapter.DefaultRateWindowSeconds;

        /// <summary>Optional monotonic clock for HTTP rate accounting (os.clock owns its clock).</summary>
        public Func<double> RbxMonotonicClock;

        /// <summary>
        /// When false, the low-level WorldEdit build/edit APIs (<c>coreai_world_spawn</c>/... and the
        /// component-edit surface) are NOT registered even though the WorldEdit capability itself may
        /// stay granted (the Rbx surface still needs the capability for <c>Instance.new</c>). Read-tier
        /// world queries are unaffected. Default true = full classic surface.
        /// </summary>
        public bool RegisterWorldEditBuildBindings = true;

        // ---- Runtime services (fed into LuaCsModRuntime / LuaCsGameToolExecutor) -----------------

        /// <summary>Persistent per-mod k/v store backing <c>store_set</c>/<c>store_get</c>.</summary>
        public ILuaModStore ModStore;

        /// <summary>
        /// Package store persisting mod source + manifest so mods survive a restart and can be shared
        /// (backs <c>ExportMod</c>/<c>ImportMod</c>/<c>RehydrateFromStore</c>). Distinct from
        /// <see cref="ModStore"/> (per-mod runtime k/v). Null =&gt; <see cref="NullLuaModSourceStore.Instance"/>
        /// (in-memory only). The mod's revision history reuses <see cref="LuaScriptVersions"/>, keyed by the
        /// runtime's <c>mod:</c> prefix so it never collides with one-off <c>execute_lua</c> script slots.
        /// </summary>
        public ILuaModSourceStore ModSourceStore;

        /// <summary>
        /// When true (default), a successful load/reload persists source + manifest to
        /// <see cref="ModSourceStore"/> and unload marks the stored package dormant. A mod unloaded
        /// because the actor it ran as disconnected leaves its package as it was.
        /// </summary>
        public bool AutoPersistMods = true;

        /// <summary>Runtime logger for load/unload/error diagnostics.</summary>
        public ILog Log;

        /// <summary>
        /// Optional mod-log ring buffer the persistent runtime appends <c>print</c>/<c>report</c>
        /// output, handler/dispatch failures, load (parse) failures, and quarantine events to — the
        /// data the <c>get_mod_logs</c> tool reads back for the self-repair loop. With
        /// <see cref="RbxApi"/> set, a mod's <c>warn</c> is appended here at
        /// <see cref="LuaLogLevel.Warn"/> too. Null = only the Unity-console/event pipeline, and
        /// <c>warn</c> keeps whatever log the Roblox surface already has (its log sink by default).
        /// </summary>
        public ILuaLogService LogService;

        /// <summary>Observer notified by the one-off <c>execute_lua</c> executor (null =&gt; no-op).</summary>
        public ILuaExecutionObserver ExecutionObserver;

        /// <summary>Optional production sink for aggregated Lua runtime counters.</summary>
        public IRbxRuntimeObservabilitySink Observability;

        /// <summary>
        /// Composition-configurable default per-resume coroutine budget (instruction-step cap and
        /// wall-clock cap) — see <see cref="CoreAI.Sandbox.LuaCs.LuaCsCoroutineBudgetSettings"/>. Null
        /// builds a settings object holding CoreAI's documented defaults. Pass the SAME instance
        /// <see cref="RbxApi"/> already exposes as <c>RbxApi.CoroutineResumeBudget</c> so both surfaces
        /// (and the <c>ScriptContext:SetTimeout</c> Lua binding, which mutates that instance) agree on
        /// one live budget rather than tracking independent copies.
        /// </summary>
        public CoreAI.Sandbox.LuaCs.LuaCsCoroutineBudgetSettings CoroutineResumeBudget;

        /// <summary>Confirmed pre-mutation backup gate shared by runtime mutation tools.</summary>
        public IConfirmedWorldMutationGate WorldMutationGate;

        /// <summary>
        /// Resolves the trusted host/local actor used when a caller runs Lua through the plain
        /// <c>ExecuteAsync(code, token)</c> seam (demos, self-tests, host scripts). In an ACL-versioned
        /// world that path needs a server-generated envelope like every other production entry; without
        /// a resolver such calls are refused.
        /// </summary>
        public Func<ActorContext> LocalActorResolver;

        // ---- Capability ceilings & guard budgets ------------------------------------------------

        /// <summary>
        /// Capability ceiling for persistent mods. A mod's per-load grant is intersected with this, so a
        /// scope can cap what any mod may ever reach regardless of the grant requested at load time.
        /// </summary>
        public LuaCapabilities Capabilities = LuaCapabilities.All;

        /// <summary>
        /// Fixed capability tier for the one-off <c>execute_lua</c> executor. It is applied on top of
        /// <see cref="Capabilities"/>, so the one-off surface gets <c>Capabilities &amp; OneOffCapabilities</c>.
        /// </summary>
        public LuaCapabilities OneOffCapabilities = LuaCapabilities.All;

        /// <summary>
        /// Wall-clock budget per persistent handler/timer call and, with <see cref="RbxApi"/> set, per
        /// resume of a mod's main chunk. <c>task.*</c> threads and signal handlers use
        /// <see cref="CoroutineResumeBudget"/> instead.
        /// </summary>
        public int HandlerTimeoutMs = LuaCsModRuntime.DefaultHandlerTimeoutMs;

        /// <summary>
        /// Instruction budget per persistent handler/timer call and, with <see cref="RbxApi"/> set, per
        /// resume of a mod's main chunk. <c>task.*</c> threads and signal handlers use
        /// <see cref="CoroutineResumeBudget"/> instead.
        /// </summary>
        public long HandlerMaxSteps = LuaCsModRuntime.DefaultHandlerMaxSteps;

        /// <summary>
        /// Per-actor persistent mod capacity. Defaults to the existing production limit; benchmark hosts may set
        /// <see cref="LuaCsModRuntime.BenchmarkMaxMods"/>. The runtime always retains its independent
        /// <see cref="LuaCsModRuntime.EmergencyMaxMods"/> ceiling.
        /// </summary>
        public int MaxMods = LuaCsModRuntime.DefaultMaxMods;

        /// <summary>Per-actor live scheduler-thread capacity.</summary>
        public int MaxSchedulerThreadsPerActor = ModScheduler.DefaultMaxThreadsPerActor;

        /// <summary>Per-actor capacity for live instances registered by runtime scripts.</summary>
        public int MaxRegisteredInstancesPerActor = LuaCsModRuntime.DefaultMaxRegisteredInstancesPerActor;

        /// <summary>Per-actor capacity for distinct named-event subscriptions.</summary>
        public int MaxEventSubscriptionsPerActor = LuaCsModRuntime.DefaultMaxEventSubscriptionsPerActor;

        /// <summary>
        /// Consecutive-error streak at which a persistent mod is quarantined — dispatch suspended, mod
        /// kept loaded and repairable via reload. A failed hook/timer call adds one and a successful one
        /// resets it; scheduler threads count per frame (a frame with any fault adds one, a frame whose
        /// threads ran cleanly resets it). See <see cref="LuaCsModRuntime.MaxErrorsBeforeQuarantine"/>.
        /// </summary>
        public int MaxErrorsBeforeQuarantine = LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine;

        /// <summary>
        /// Budget trips in a row (instruction, time or memory budget) at which a persistent mod is
        /// quarantined and its stored package suspended, so the next start does not run it again. See
        /// <see cref="LuaCsModRuntime.MaxBudgetTripsBeforeQuarantine"/>.
        /// </summary>
        public int MaxBudgetTripsBeforeQuarantine = LuaCsModRuntime.DefaultMaxBudgetTripsBeforeQuarantine;

        /// <summary>
        /// Live-heap growth allowed in one execution of a mod's code (the allocation-bomb backstop): every
        /// guarded hook/timer call and, with <see cref="RbxApi"/> set, every resume of the mod's main
        /// chunk, its <c>task.*</c> threads and its signal handlers. It starts over with each call or
        /// resume and never accumulates across them. A trip cuts that call or resume and is charged toward
        /// <see cref="MaxErrorsBeforeQuarantine"/> like any failure. Defaults to
        /// <see cref="LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget"/>.
        /// </summary>
        public long HandlerMaxAllocatedBytes = LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget;

        /// <summary>
        /// Optional per-scene/host gameplay bindings registered IN ADDITION to the built-in world/data/prefab
        /// surface, on BOTH the persistent runtime and the one-off executor. Lets a scene inject its own Lua
        /// APIs (e.g. a demo's <c>forge_define</c>/<c>forge_spawn</c>) through the same
        /// <c>Action&lt;LuaCsApiRegistry, LuaCapabilities&gt;</c> seam without replacing the core surface. It runs
        /// AFTER the built-in bindings, so it may add to or override them. Register your names against a value
        /// resolved LAZILY (at call time) if the backing scene object is not ready at scope-build. Null = none.
        /// <para>
        /// The <see cref="LuaCapabilities"/> argument is the EFFECTIVE set the built-in surface used for the
        /// same registry, never the raw request: for a persistent mod it is <see cref="Capabilities"/>
        /// intersected with the mod's requested grant; for the one-off executor it is
        /// <see cref="Capabilities"/> intersected with <see cref="OneOffCapabilities"/>. Gate privileged APIs
        /// on it (e.g. <c>(caps &amp; LuaCapabilities.Full) != 0</c>) exactly as the built-in tiers do.
        /// </para>
        /// </summary>
        public Action<LuaCsApiRegistry, LuaCapabilities> AdditionalGameplayBindings;

        /// <summary>
        /// Host frame port for the one-off <c>execute_lua</c> path. A chunk may legitimately run for
        /// seconds before its wall-clock budget cuts it; on a single-threaded player that is the whole
        /// page, so the guard releases the frame every few milliseconds through this port. Null (the
        /// default) keeps the previous blocking behaviour, which is what a headless or Edit Mode fixture
        /// with no running loop wants.
        /// </summary>
        public IScriptFrameYielder FrameYielder;
    }

    /// <summary>
    /// The fully-wired Lua-CSharp mod stack produced by <see cref="LuaCsModRuntimeFactory"/>: the
    /// persistent tick <see cref="LuaCsModRuntime"/>, the one-off <see cref="LuaCsGameToolExecutor"/>, and
    /// the shared <see cref="LuaCsGameplayBindings"/> both are wired to. A DI scope would register each of
    /// these as a component (the runtime and executor as the long-lived services, the bindings as the
    /// shared transaction-scoped singleton).
    /// </summary>
    public sealed class LuaCsModStack
    {
        private readonly Func<LuaCsModStack> _activeProvider;
        private readonly LuaCsModRuntime _runtime;
        private readonly LuaCsGameToolExecutor _toolExecutor;
        private readonly LuaCsGameplayBindings _gameplayBindings;

        public LuaCsModStack(
            LuaCsModRuntime runtime,
            LuaCsGameToolExecutor toolExecutor,
            LuaCsGameplayBindings gameplayBindings)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
            _gameplayBindings = gameplayBindings ?? throw new ArgumentNullException(nameof(gameplayBindings));
        }

        internal LuaCsModStack(Func<LuaCsModStack> activeProvider)
        {
            _activeProvider = activeProvider ?? throw new ArgumentNullException(nameof(activeProvider));
        }

        /// <summary>Persistent, ticked mod runtime (long-lived mods with hooks/timers/store).</summary>
        public LuaCsModRuntime Runtime => Active._runtime;

        /// <summary>One-off <c>execute_lua</c> executor sharing the same gameplay bindings.</summary>
        public LuaCsGameToolExecutor ToolExecutor => Active._toolExecutor;

        /// <summary>Shared capability-scoped gameplay bindings both surfaces register through.</summary>
        public LuaCsGameplayBindings GameplayBindings => Active._gameplayBindings;

        private LuaCsModStack Active
        {
            get
            {
                if (_activeProvider == null)
                {
                    return this;
                }

                LuaCsModStack active = _activeProvider();
                if (active == null || ReferenceEquals(active, this))
                {
                    throw new InvalidOperationException("The active Lua world session is unavailable.");
                }

                return active;
            }
        }
    }

    /// <summary>
    /// Composition helper that assembles the additive Lua-CSharp mod stack from host services. This is the
    /// reusable wiring a later <c>CoreAiModsLifetimeScope</c> will call: it builds a single
    /// <see cref="LuaCsGameplayBindings"/> at the configured capability ceiling and feeds it into BOTH the
    /// persistent <see cref="LuaCsModRuntime"/> (via its <c>Action&lt;LuaCsApiRegistry, LuaCapabilities&gt;</c>
    /// seam) and the one-off <see cref="LuaCsGameToolExecutor"/> (via a small adapter, since the executor
    /// consumes the VM-agnostic <see cref="ILuaCsGameRuntimeBindings"/> single-arg shape). Sharing one
    /// bindings instance means the persistent and one-off paths observe the same world-transaction scope,
    /// exactly as the MoonSharp side shares <c>IGameLuaRuntimeBindings</c> between its runtime and executor.
    /// </summary>
    public static class LuaCsModRuntimeFactory
    {
        /// <summary>Builds the wired stack from the supplied options. Only <paramref name="options"/> is required.</summary>
        public static LuaCsModStack Create(LuaCsModStackOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            // WHY: warn belongs in the mod's own log next to its print output, where get_mod_logs and the
            // repair loop read it; unattached, the Rbx surface sent every mod's warn to the host's log
            // sink. Only a configured log is attached, so a stack built without one leaves a log the host
            // attached to the bindings itself in place instead of detaching it.
            if (options.RbxApi != null && options.LogService != null)
            {
                options.RbxApi.AttachModLog(options.LogService);
            }

            LuaCsGameplayBindings bindings = new(
                options.Logger,
                options.LuaScriptVersions,
                options.DataOverlayVersions,
                options.CommandSink,
                options.PrefabRegistry,
                options.AllowedScenes,
                fullBlacklistPolicy: options.FullBlacklistPolicy,
                allowNonPublicFullMembers: options.AllowNonPublicFullMembers,
                capabilities: options.Capabilities,
                rbxApi: options.RbxApi,
                registerWorldEditBuildBindings: options.RegisterWorldEditBuildBindings,
                rbxHttpPolicy: options.RbxHttpPolicy,
                rbxHttpTransport: options.RbxHttpTransport,
                rbxHttpResolver: options.RbxHttpResolver,
                rbxHttpRequestsPerWindow: options.RbxHttpRequestsPerWindow,
                rbxHttpRateWindowSeconds: options.RbxHttpRateWindowSeconds,
                rbxMonotonicClock: options.RbxMonotonicClock);

            // WHY: The factory is the composition root: it wires the Lua-CSharp engine as THE single
            // IScriptEngine of the stack, so nothing above the Scripting/ adapter layer creates a VM
            // state directly and a future engine swap happens here alone.
            LuaCsScriptEngine engine = new(
                observability: options.Observability,
                coroutineResumeBudget: options.CoroutineResumeBudget);

            // WHY: Register the built-in surface first, then any host/per-scene additions, through the SAME
            // seam, so an injected demo API (forge_define/...) reaches every loaded mod alongside the core APIs.
            // The third argument is the owning mod's id; ownership-tracked surfaces (logic slots) use it so a
            // mod's registrations can be torn down on unload/reload/quarantine.
            Action<IScriptFunctionRegistry, LuaCapabilities, string> registerAll =
                options.AdditionalGameplayBindings == null
                    ? bindings.Register
                    : (registry, caps, ownerModId) =>
                    {
                        bindings.Register(registry, caps, ownerModId);

                        // WHY: The compatibility field is typed against the concrete Lua-CSharp registry; this
                        // stack only ever creates registries via the Lua-CSharp engine, so the cast is exact.
                        // The extension gets the tiers the built-in surface just registered, not the mod's raw
                        // request: a mod asking for Full under a ceiling without Full must not reach a host
                        // API gated on the Full bit.
                        options.AdditionalGameplayBindings(
                            (LuaCsApiRegistry)registry, bindings.EffectiveCapabilities(caps));
                    };

            LuaCsModRuntime runtime = new(
                registerAll,
                options.ModStore,
                options.Log,
                options.HandlerTimeoutMs,
                options.HandlerMaxSteps,
                options.ModSourceStore,
                options.AutoPersistMods,
                // Share the version store the gameplay bindings already use: the runtime keys mod history
                // under a "mod:" prefix, so it never collides with the coreai_lua_* script slots.
                options.LuaScriptVersions,
                // WHY: The bindings are the shared transaction scope of both surfaces; handing them to the
                // runtime lets it reset a leaked coreai_world_begin per guarded call, exactly as the
                // one-off executor resets around every chunk.
                bindings,
                options.HandlerMaxAllocatedBytes,
                engine,
                options.MaxErrorsBeforeQuarantine,
                // WHY: Handing the shared slot surface to the runtime closes the teardown loop: a mod's
                // logic_define overrides are cleared on unload/reload/quarantine and override failures are
                // attributed into the mod's diagnostics channel.
                bindings.LogicSlots,
                options.LogService,
                options.RbxApi,
                options.MaxMods,
                options.Observability,
                options.MaxSchedulerThreadsPerActor,
                options.MaxRegisteredInstancesPerActor,
                options.MaxEventSubscriptionsPerActor,
                maxBudgetTripsBeforeQuarantine: options.MaxBudgetTripsBeforeQuarantine);

            if (options.RbxApi != null)
            {
                WireRbxTeardown(runtime, options.RbxApi, options.Log);
            }

            LuaCsGameToolExecutor executor = new(
                engine.Environment,
                new CapabilityScopedGameRuntimeBindings(
                    bindings, options.OneOffCapabilities, options.AdditionalGameplayBindings),
                options.ExecutionObserver ?? new NullLuaExecutionObserver(),
                options.Observability,
                options.WorldMutationGate,
                engine.CoroutineResumeBudget);
            executor.LocalActorResolver = options.LocalActorResolver;
            executor.FrameYielder = options.FrameYielder;

            return new LuaCsModStack(runtime, executor, bindings);
        }

        /// <summary>
        /// Releases what a mod run made through the Roblox surface whenever the runtime tears that run
        /// down: its scheduler threads (only the outgoing generation's on a reload), its signal
        /// connections (likewise), and, on unload, every instance it owns.
        /// </summary>
        /// <remarks>
        /// WHY here, by default: a host that composed its stack from this factory alone got a reload
        /// that left the replaced run's task loops, Heartbeat handlers and tweens running next to the
        /// new run's, one more set per save. Every host needs this teardown, so it belongs to the
        /// composition root rather than to each host. WHY a host that wires the same teardown itself
        /// is unaffected: this one runs after every <see cref="LuaCsModRuntime.ModTearingDown"/>
        /// subscriber and each step only releases what is still held.
        /// </remarks>
        private static void WireRbxTeardown(LuaCsModRuntime runtime, LuaCsRbxApiBindings rbxApi, ILog log)
        {
            ModConnectionRegistry connections = rbxApi.Connections;
            InstanceRegistry registry = rbxApi.Registry;
            runtime.SetDefaultTeardown((modId, reason) =>
            {
                // WHY: on a reload the replacement chunk has already run and connected (BuildMod runs
                // before the teardown), so only the outgoing generation goes; an unload or a quarantine
                // has no new chunk, so everything goes.
                if (reason == LuaModTeardownReason.Reload)
                {
                    rbxApi.KillOutgoingScheduledGenerations(modId);
                }
                else
                {
                    rbxApi.KillAllScheduledOwnedBy(modId);
                }

                connections.DisconnectOwnedBy(modId, reason == LuaModTeardownReason.Reload);
                if (reason != LuaModTeardownReason.Unload)
                {
                    return;
                }

                foreach (RbxInstance owned in registry.GetTeardownOwnedBy(modId))
                {
                    try
                    {
                        owned?.Destroy();
                    }
                    catch (Exception ex)
                    {
                        log?.Warn($"[LuaCsModRuntimeFactory] Destroying an instance owned by unloaded mod '{modId}' failed: {ex.Message}");
                    }
                }
            });
        }

        /// <summary>
        /// Adapts the runtime's two-arg <see cref="LuaCsGameplayBindings"/> into the one-off executor's
        /// VM-agnostic <see cref="ILuaCsGameRuntimeBindings"/> at a fixed capability tier, forwarding the
        /// transaction-reset seam so a leaked <c>coreai_world_begin</c> can be cleared between chunks. The
        /// optional host extension sees the same effective tiers as the built-in surface (host ceiling
        /// intersected with the one-off tier).
        /// </summary>
        private sealed class CapabilityScopedGameRuntimeBindings : ILuaCsGameRuntimeBindings,
            IActorScopedLuaCsGameRuntimeBindings, ILuaTransactionScope
        {
            private readonly LuaCsGameplayBindings _bindings;
            private readonly LuaCapabilities _capabilities;
            private readonly Action<LuaCsApiRegistry, LuaCapabilities> _additional;

            public CapabilityScopedGameRuntimeBindings(
                LuaCsGameplayBindings bindings,
                LuaCapabilities capabilities,
                Action<LuaCsApiRegistry, LuaCapabilities> additional = null)
            {
                _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
                _capabilities = capabilities;
                _additional = additional;
            }

            public void RegisterGameplayApis(LuaCsApiRegistry registry)
            {
                _bindings.Register(registry, _capabilities);
                RegisterAdditional(registry);
            }

            public InstanceRegistry MutationRegistry => _bindings.RbxApi?.Registry;

            public void RegisterGameplayApis(LuaCsApiRegistry registry,
                ActorContext actorContext)
            {
                _bindings.Register(registry, _capabilities, null, actorContext);
                RegisterAdditional(registry);
            }

            public void RegisterGameplayApis(LuaCsApiRegistry registry,
                ActorContext actorContext, MutationEnvelope mutationEnvelope)
            {
                _bindings.Register(registry, _capabilities, null,
                    actorContext, mutationEnvelope);
                RegisterAdditional(registry);
            }

            private void RegisterAdditional(LuaCsApiRegistry registry)
            {
                // WHY: _capabilities is only the one-off tier; the built-in surface above also applied the
                // host ceiling, so the extension must receive that same intersection or a one-off tier
                // wider than the ceiling would hand it tiers (Full) the host never granted.
                _additional?.Invoke(registry, _bindings.EffectiveCapabilities(_capabilities));
            }

            public void ResetTransactions()
            {
                ((ILuaTransactionScope)_bindings).ResetTransactions();
            }

            public void PushTransactionScope()
            {
                ((ILuaTransactionScope)_bindings).PushTransactionScope();
            }

            public void PopTransactionScope()
            {
                ((ILuaTransactionScope)_bindings).PopTransactionScope();
            }
        }
    }
}
