using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Sandbox.LuaCs;

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

        /// <summary>Optional data-overlay payload validator (null =&gt; DefaultDataOverlayPayloadValidator).</summary>
        public IDataOverlayPayloadValidator DataOverlayValidator;

        /// <summary>Optional Full-tier reflection allow/deny policy (null =&gt; allow-all).</summary>
        public IFullLuaAccessBlacklistPolicy FullBlacklistPolicy;

        /// <summary>When true, Full-tier reflection may touch non-public members.</summary>
        public bool AllowNonPublicFullMembers;

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

        /// <summary>Observer notified by the one-off <c>execute_lua</c> executor (null =&gt; no-op).</summary>
        public ILuaExecutionObserver ExecutionObserver;

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
                options.DataOverlayValidator,
                options.FullBlacklistPolicy,
                options.AllowNonPublicFullMembers,
                options.Capabilities);

            LuaCsModRuntime runtime = new(
                gameplayBindings: bindings.Register,
                store: options.ModStore,
                log: options.Log,
                handlerTimeoutMs: options.HandlerTimeoutMs,
                handlerMaxSteps: options.HandlerMaxSteps,
                sourceStore: options.ModSourceStore,
                autoPersistMods: options.AutoPersistMods,
                // Share the version store the gameplay bindings already use: the runtime keys mod history
                // under a "mod:" prefix, so it never collides with the coreai_lua_* script slots.
                versionStore: options.LuaScriptVersions);

            LuaCsGameToolExecutor executor = new(
                new LuaCsSecureEnvironment(),
                new CapabilityScopedGameRuntimeBindings(bindings, options.OneOffCapabilities),
                options.ExecutionObserver ?? new NullLuaExecutionObserver());

            return new LuaCsModStack(runtime, executor, bindings);
        }

        /// <summary>
        /// Adapts the runtime's two-arg <see cref="LuaCsGameplayBindings"/> into the one-off executor's
        /// VM-agnostic <see cref="ILuaCsGameRuntimeBindings"/> at a fixed capability tier, forwarding the
        /// transaction-reset seam so a leaked <c>coreai_world_begin</c> can be cleared between chunks.
        /// </summary>
        private sealed class CapabilityScopedGameRuntimeBindings : ILuaCsGameRuntimeBindings, ILuaTransactionScope
        {
            private readonly LuaCsGameplayBindings _bindings;
            private readonly LuaCapabilities _capabilities;

            public CapabilityScopedGameRuntimeBindings(LuaCsGameplayBindings bindings, LuaCapabilities capabilities)
            {
                _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
                _capabilities = capabilities;
            }

            public void RegisterGameplayApis(LuaCsApiRegistry registry)
            {
                _bindings.Register(registry, _capabilities);
            }

            public void ResetTransactions()
            {
                ((ILuaTransactionScope)_bindings).ResetTransactions();
            }
        }
    }
}
