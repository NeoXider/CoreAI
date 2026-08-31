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
        /// the same instance world. Null = the Roblox globals are not installed.
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

        /// <summary>Optional monotonic clock shared by os.clock and HTTP rate accounting.</summary>
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
        /// <see cref="ModSourceStore"/> and unload marks the stored package dormant.
        /// </summary>
        public bool AutoPersistMods = true;

        /// <summary>Runtime logger for load/unload/error diagnostics.</summary>
        public ILog Log;

        /// <summary>
        /// Optional mod-log ring buffer the persistent runtime appends <c>print</c>/<c>report</c>
        /// output, handler/dispatch failures, load (parse) failures, and quarantine events to — the
        /// data the <c>get_mod_logs</c> tool reads back for the self-repair loop. Null = only the
        /// Unity-console/event pipeline (the previous behavior).
        /// </summary>
        public ILuaLogService LogService;

        /// <summary>Observer notified by the one-off <c>execute_lua</c> executor (null =&gt; no-op).</summary>
        public ILuaExecutionObserver ExecutionObserver;

        /// <summary>Optional production sink for aggregated Lua runtime counters.</summary>
        public IRbxRuntimeObservabilitySink Observability;

        // ---- Capability ceilings & guard budgets ------------------------------------------------

        /// <summary>
        /// Capability ceiling for persistent mods. A mod's per-load grant is intersected with this, so a
        /// scope can cap what any mod may ever reach regardless of the grant requested at load time.
        /// </summary>
        public LuaCapabilities Capabilities = LuaCapabilities.All;

        /// <summary>Fixed capability tier for the one-off <c>execute_lua</c> executor.</summary>
        public LuaCapabilities OneOffCapabilities = LuaCapabilities.All;

        /// <summary>Wall-clock budget per persistent handler/timer call.</summary>
        public int HandlerTimeoutMs = LuaCsModRuntime.DefaultHandlerTimeoutMs;

        /// <summary>Instruction budget per persistent handler/timer call.</summary>
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
        /// Consecutive-error streak (reset by any success) at which a persistent mod is quarantined —
        /// dispatch suspended, mod kept loaded and repairable via reload. See
        /// <see cref="LuaCsModRuntime.MaxErrorsBeforeQuarantine"/>.
        /// </summary>
        public int MaxErrorsBeforeQuarantine = LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine;

        /// <summary>
        /// Per-handler/timer-call GC allocation budget (the process-heap allocation-bomb backstop). A trip
        /// cuts the offending call and is charged to the same consecutive-error quarantine streak as any
        /// failure (reset on success). Defaults to
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
        /// </summary>
        public Action<LuaCsApiRegistry, LuaCapabilities> AdditionalGameplayBindings;
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
        public LuaCsModStack(
            LuaCsModRuntime runtime,
            LuaCsGameToolExecutor toolExecutor,
            LuaCsGameplayBindings gameplayBindings)
        {
            Runtime = runtime;
            ToolExecutor = toolExecutor;
            GameplayBindings = gameplayBindings;
        }

        /// <summary>Persistent, ticked mod runtime (long-lived mods with hooks/timers/store).</summary>
        public LuaCsModRuntime Runtime { get; }

        /// <summary>One-off <c>execute_lua</c> executor sharing the same gameplay bindings.</summary>
        public LuaCsGameToolExecutor ToolExecutor { get; }

        /// <summary>Shared capability-scoped gameplay bindings both surfaces register through.</summary>
        public LuaCsGameplayBindings GameplayBindings { get; }
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
            LuaCsScriptEngine engine = new(observability: options.Observability);

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
                        options.AdditionalGameplayBindings((LuaCsApiRegistry)registry, caps);
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
                options.MaxEventSubscriptionsPerActor);

            LuaCsGameToolExecutor executor = new(
                engine.Environment,
                new CapabilityScopedGameRuntimeBindings(
                    bindings, options.OneOffCapabilities, options.AdditionalGameplayBindings),
                options.ExecutionObserver ?? new NullLuaExecutionObserver(),
                options.Observability);

            return new LuaCsModStack(runtime, executor, bindings);
        }

        /// <summary>
        /// Adapts the runtime's two-arg <see cref="LuaCsGameplayBindings"/> into the one-off executor's
        /// VM-agnostic <see cref="ILuaCsGameRuntimeBindings"/> at a fixed capability tier, forwarding the
        /// transaction-reset seam so a leaked <c>coreai_world_begin</c> can be cleared between chunks.
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
                _additional?.Invoke(registry, _capabilities);
            }

            public InstanceRegistry MutationRegistry => _bindings.RbxApi?.Registry;

            public void RegisterGameplayApis(LuaCsApiRegistry registry,
                ActorContext actorContext, MutationEnvelope mutationEnvelope)
            {
                _bindings.Register(registry, _capabilities, null,
                    actorContext, mutationEnvelope);
                _additional?.Invoke(registry, _capabilities);
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
