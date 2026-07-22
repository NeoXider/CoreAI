using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Logging;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Newtonsoft.Json;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Why a mod instance's runtime side effects (logic-slot overrides, future instance registries /
    /// signals) are being torn down. Carried by <see cref="LuaCsModRuntime.ModTearingDown"/>.
    /// </summary>
    public enum LuaModTeardownReason
    {
        /// <summary>The mod is being removed from the runtime (<see cref="LuaCsModRuntime.UnloadMod"/>).</summary>
        Unload,

        /// <summary>The mod is being replaced by a new instance (<see cref="LuaCsModRuntime.ReloadMod"/>); fired before the swap.</summary>
        Reload,

        /// <summary>The mod hit its consecutive-error threshold and enters quarantine (kept loaded, dispatch suspended).</summary>
        Quarantine
    }

    /// <summary>
    /// Lua-CSharp (nuskey8/Lua-CSharp) persistent runtime for long-lived mods. This is the ADDITIVE
    /// counterpart of the MoonSharp <c>CoreAI.Ai.LuaModRuntime</c>, built as part of the
    /// MoonSharp -> Lua-CSharp migration: both VMs coexist and the tick driver can later swap
    /// <c>LuaModRuntime</c> -> <c>LuaCsModRuntime</c> by type because the public lifecycle/tick/
    /// diagnostics surface below mirrors the MoonSharp runtime.
    ///
    /// A mod is a sandboxed Lua-CSharp <see cref="IScriptState"/> that registers hooks during load and
    /// then lives across frames:
    /// <list type="bullet">
    /// <item><c>hooks_on(event, fn)</c> — handler for named events (from the game or other mods).</item>
    /// <item><c>hooks_every(seconds, fn)</c> — repeating timer driven by <see cref="Tick"/>.</item>
    /// <item><c>events_emit(name, payload)</c> — emits an event to the game (<see cref="ModEventEmitted"/>) and other mods.</item>
    /// <item><c>store_set(key, value)</c> / <c>store_get(key)</c> — persistent per-mod k/v (when an <see cref="ILuaModStore"/> is supplied).</item>
    /// <item><c>mod_id()</c> — the mod's own id.</item>
    /// <item><c>mods_export/mods_get/mods_call/mods_list_exports</c> — cross-mod, plain-data-copied surface.</item>
    /// </list>
    /// The host calls <see cref="Tick"/> once per frame; every handler/timer call runs under a
    /// per-call instruction/time guard (<see cref="LuaCsExecutionGuard"/>).
    ///
    /// ERROR POLICY — QUARANTINE, NOT UNLOAD: a mod failing <see cref="MaxErrorsBeforeQuarantine"/>
    /// times in a row (the counter resets on a successful call) is QUARANTINED: it stops dispatching
    /// (handlers, timers, and queued events are all skipped and its logic-slot overrides revert to
    /// vanilla) but it STAYS loaded and fully addressable — <c>manage_mods list/get_source/diagnostics</c>
    /// keep seeing it and <see cref="ReloadMod"/> works normally, clearing the quarantine and the error
    /// streak. This keeps the async repair loop honest: an LLM repair that takes seconds or minutes
    /// still finds the mod it was asked to fix instead of a "not loaded" error after an auto-unload.
    ///
    /// SCOPE NOTE (migration pass 1): the heavy world/unity gameplay bindings that the MoonSharp
    /// runtime injects via <c>IGameLuaRuntimeBindings.RegisterGameplayApis(LuaApiRegistry)</c> are
    /// NOT ported here. This runtime accepts an injection callback so ported gameplay APIs can be
    /// wired later; see <see cref="RegisterGameplayBindings"/> for the open seam.
    ///
    /// PERSISTENCE PARITY (migration pass 2): source-store persistence, version history, import/export,
    /// rehydrate and forget/revert are now ported from the MoonSharp runtime and behave identically
    /// (a successful <see cref="LoadMod"/>/<see cref="ReloadMod"/> saves source+manifest to the
    /// <see cref="ILuaModSourceStore"/> and records a revision in the <see cref="ILuaScriptVersionStore"/>;
    /// <see cref="UnloadMod"/> marks the package dormant; <see cref="ForgetMod"/> deletes it), so
    /// <c>manage_mods</c> can later run on this VM. Both stores default to no-op implementations, so a
    /// host that wires neither keeps the prior in-memory-only behaviour.
    /// </summary>
    public sealed class LuaCsModRuntime : ILuaModRuntime
    {
        public const int DefaultHandlerTimeoutMs = 500;
        public const long DefaultHandlerMaxSteps = 100_000;
        public const int DefaultMaxMods = 32;
        public const int DefaultMaxHandlersPerMod = 64;
        public const int DefaultMaxTimersPerMod = 16;
        public const int DefaultMaxQueuedEventsPerMod = 256;
        public const int DefaultMaxEventsDispatchedPerTick = 64;

        /// <summary>
        /// Upper bound on handler invocations dispatched across <em>all</em> mods in a single
        /// <see cref="Tick"/>. Chosen as 4x the per-mod cap: comfortably above the per-mod cap so a
        /// single busy mod is never throttled below its own budget, while still bounding a worst-case
        /// burst across many mods to a few hundred calls per frame. Mods not reached once it is
        /// exhausted keep their queued events for later ticks (no events are dropped).
        /// </summary>
        public const int DefaultMaxEventsDispatchedPerTickGlobal = 256;

        /// <summary>
        /// Default consecutive-error streak (reset by any successful call) at which a mod is
        /// quarantined — suspended from dispatch but kept loaded so it can be inspected and repaired
        /// via <see cref="ReloadMod"/>. Overridable per runtime via the constructor /
        /// <c>LuaCsModStackOptions.MaxErrorsBeforeQuarantine</c>.
        /// </summary>
        public const int DefaultMaxErrorsBeforeQuarantine = 8;

        /// <summary>Maximum values/functions one mod may publish via <c>mods_export</c>.</summary>
        public const int DefaultMaxExportsPerMod = 64;

        /// <summary>
        /// Maximum nested <c>mods_call</c> depth (A calls B calls C ...). Bounds accidental
        /// cross-mod recursion with a clear error instead of a Lua stack overflow.
        /// </summary>
        public const int MaxCrossCallDepth = 8;

        /// <summary>Maximum table nesting marshalled across mods by <c>mods_get</c>/<c>mods_call</c>.</summary>
        public const int CrossModTableDepth = 4;

        /// <summary>Shortest accepted <c>hooks_every</c> interval, so timers cannot degenerate into per-instruction spam.</summary>
        public const double MinTimerIntervalSeconds = 0.05;

        /// <summary>
        /// Prefix applied to a mod id when forming its <see cref="ILuaScriptVersionStore"/> key, so a mod's
        /// revision history shares the version store with one-shot <c>execute_lua</c> scripts without ever
        /// colliding with a game-defined script slot of the same name. Mirrors the MoonSharp runtime's key.
        /// </summary>
        public const string VersionKeyPrefix = "mod:";

        /// <summary>
        /// Upper bound on the number of recent Tick-time handler errors retained for the agent to
        /// inspect via <see cref="GetRecentHandlerErrors"/>. Oldest entries are dropped once the buffer
        /// is full so a perpetually broken mod cannot grow it without bound.
        /// </summary>
        public const int MaxRetainedHandlerErrors = 32;

        /// <summary>
        /// Upper bound on the number of recent <c>report()</c>/<c>print()</c> emissions retained for
        /// inspection via <see cref="GetRecentReports"/>, independent of each mod's <c>LogReports</c>
        /// mute flag. Oldest entries are dropped once the buffer is full so a chatty mod cannot grow it
        /// without bound.
        /// </summary>
        public const int MaxRetainedReports = 64;

        private sealed class TimerEntry
        {
            public double IntervalSeconds;
            public double DueIn;
            public object Fn;
        }

        private sealed class Mod
        {
            public string Id = "";
            public IScriptState State;
            public string Source = "";
            public LuaCapabilities Caps;
            public bool LogReports;
            public readonly Dictionary<string, List<object>> Handlers = new(StringComparer.Ordinal);
            public readonly List<TimerEntry> Timers = new();
            public readonly Queue<KeyValuePair<string, string>> Pending = new();
            public readonly Dictionary<string, object> Exports = new(StringComparer.Ordinal);
            public int HandlerCount;
            public int ErrorCount;
            public DateTime LoadedAtUtc;

            /// <summary>
            /// True once the mod hit the consecutive-error threshold: it stays loaded and addressable
            /// but is skipped by <see cref="Tick"/> (no handlers, timers, or queued events) until a
            /// <see cref="ReloadMod"/> replaces it with a fresh, un-quarantined instance.
            /// </summary>
            public bool Quarantined;
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, Mod> _mods = new(StringComparer.Ordinal);
        private readonly IScriptEngine _engine;
        private readonly IValueMarshaller _marshaller;
        private readonly IScriptExecutionGuard _handlerGuard;
        private readonly Action<IScriptFunctionRegistry, LuaCapabilities, string> _gameplayBindings;
        private readonly LuaCsLogicSlots _logicSlots;
        private readonly ILuaModStore _store;
        private readonly ILuaModSourceStore _sourceStore;
        private readonly ILuaScriptVersionStore _versionStore;
        private readonly ILuaTransactionScope _transactionScope;
        private readonly bool _autoPersistMods;
        private readonly ILog _log;
        private readonly List<Mod> _tickScratch = new();

        private readonly Queue<LuaModHandlerError> _recentHandlerErrors = new();
        private readonly Queue<LuaModReport> _recentReports = new();

        /// <summary>
        /// Round-robin start index for charging the global event dispatch budget so, under sustained
        /// saturation, every mod is reached over a bounded number of ticks instead of the tail
        /// starving forever.
        /// </summary>
        private int _dispatchRotation;

        // WHY: Reentrancy depth of mods_call on the current thread (ticks run on the main thread; a
        // second thread would only ever see its own chain).
        [ThreadStatic]
        private static int _crossCallDepth;

        /// <summary>
        /// Raised when a mod calls <c>events_emit(name, payload)</c>: (modId, eventName, payload).
        /// The Unity layer bridges this to MessagePipe/game systems.
        /// </summary>
        public event Action<string, string, string> ModEventEmitted;

        /// <summary>Raised after a mod source is successfully loaded or reloaded: (modId, source, caps).</summary>
        public event Action<string, string, LuaCapabilities> ModSourceLoaded;

        /// <summary>Raised after a mod is unloaded via <see cref="UnloadMod"/>/<see cref="ForgetMod"/>: (modId, source, caps). Repeated errors never unload — see <see cref="ModQuarantined"/>.</summary>
        public event Action<string, string, LuaCapabilities> ModSourceUnloaded;

        /// <summary>
        /// Raised when a mod hits <see cref="MaxErrorsBeforeQuarantine"/> consecutive errors and is
        /// quarantined: (modId, consecutiveErrorCount). The mod stays loaded but stops dispatching
        /// until it is reloaded; hosts drive their repair loop from this instead of an unload.
        /// Subscribers are isolated: a throwing subscriber never skips the rest.
        /// </summary>
        public event Action<string, int> ModQuarantined;

        /// <summary>
        /// Raised whenever a mod instance's runtime side effects are being torn down — on
        /// <see cref="UnloadMod"/>, on <see cref="ReloadMod"/> (before the new instance is swapped in),
        /// and on quarantine entry: (modId, reason). Logic-slot overrides are already cleared by the
        /// runtime itself; future subsystems (instance registries, signals) subscribe here to release
        /// the mod's effects at the same point. Subscribers are isolated.
        /// </summary>
        public event Action<string, LuaModTeardownReason> ModTearingDown;

        /// <summary>
        /// Raised when a loaded mod's hook/timer throws while running under <see cref="Tick"/>:
        /// (modId, error, consecutiveErrorCount). Fired asynchronously on the host thread; the count
        /// resets to zero after any successful call, so a host can debounce an auto-repair loop on the
        /// streak length.
        /// </summary>
        public event Action<string, string, int> ModHandlerErrored;

        /// <summary>
        /// Raised when a loaded mod calls <c>report(message)</c> (or <c>print</c>) and report logging
        /// is enabled for that mod: (modId, message). Reports are muted by default so timer mods cannot
        /// flood logs.
        /// </summary>
        public event Action<string, string> ModReportEmitted;

        /// <summary>
        /// True when the Lua-CSharp sandbox is available on this platform. Lua-CSharp is a managed,
        /// AOT-safe VM (the reason for this migration), so unlike the MoonSharp runtime this is always
        /// supported — including IL2CPP/WebGL.
        /// </summary>
        public static bool IsSupported => true;

        /// <summary>
        /// Consecutive-error streak (reset by any successful call) at which a mod is quarantined.
        /// Quarantine suspends dispatch (handlers, timers, queued events) and reverts the mod's
        /// logic-slot overrides to vanilla, but the mod stays loaded; <see cref="ReloadMod"/> clears
        /// both the quarantine and the streak.
        /// </summary>
        public int MaxErrorsBeforeQuarantine { get; }

        /// <param name="gameplayBindings">
        /// Optional seam for registering ported world/unity gameplay APIs on each mod's
        /// <see cref="IScriptFunctionRegistry"/>, scoped to the mod's granted <see cref="LuaCapabilities"/>;
        /// the third argument is the owning mod's id so ownership-tracked surfaces (logic slots) can
        /// attribute what a mod registers. Null = mods only get the built-in mod-core APIs. See
        /// <see cref="RegisterGameplayBindings"/>.
        /// </param>
        /// <param name="store">Optional persistent per-mod k/v store backing <c>store_set/get</c>.</param>
        /// <param name="log">Optional logger.</param>
        /// <param name="handlerTimeoutMs">Wall-clock budget per handler/timer call.</param>
        /// <param name="handlerMaxSteps">Instruction budget per handler/timer call.</param>
        /// <param name="sourceStore">
        /// Optional package store persisting mod source + manifest so mods survive a restart and can be
        /// shared. Distinct from <paramref name="store"/> (which is per-mod runtime k/v). Null falls back
        /// to <see cref="NullLuaModSourceStore.Instance"/> (in-memory only — the prior behaviour).
        /// </param>
        /// <param name="autoPersistMods">
        /// When true (default), a successful <see cref="LoadMod"/>/<see cref="ReloadMod"/> persists the
        /// source + manifest to <paramref name="sourceStore"/> and <see cref="UnloadMod"/> marks the
        /// stored package dormant. Persistence is always best-effort: a store failure is logged, never
        /// thrown out of the load.
        /// </param>
        /// <param name="versionStore">
        /// Optional revision tracker. When supplied, every successful <see cref="LoadMod"/>/<see cref="ReloadMod"/>
        /// records the mod's source as a new revision (keyed by <see cref="VersionKeyPrefix"/> + mod id), so
        /// the agent (or host) can list past revisions and roll back via <see cref="ListModVersions"/> /
        /// <see cref="TryRevertMod"/>. Null falls back to <see cref="NullLuaScriptVersionStore"/> (no history —
        /// the prior behaviour).
        /// </param>
        /// <param name="transactionScope">
        /// Optional shared transaction scope of the gameplay bindings behind <paramref name="gameplayBindings"/>.
        /// When supplied, the runtime resets it around every load chunk and guarded handler/timer call —
        /// mirroring <see cref="LuaCsGameToolExecutor"/> — so a handler that dies between
        /// <c>coreai_world_begin</c> and commit cannot leave a stale transaction silently buffering the
        /// world commands of later handlers/timers.
        /// </param>
        /// <param name="handlerMaxAllocatedBytes">
        /// Per-handler/timer-call GC allocation budget (the process-heap allocation-bomb backstop). A trip
        /// (<see cref="LuaCsExecutionGuard.IsMemoryBudgetTrip"/>) cuts the offending call and is charged to the
        /// same consecutive-error streak as any failure (<see cref="MaxErrorsBeforeQuarantine"/>, reset on success).
        /// This is a PER-CALL first-growth backstop, not a cross-call cumulative limiter: because
        /// GC.GetTotalMemory reports the committed-heap high-water mark, only the first oversized allocation
        /// trips — later calls reuse that committed space and no longer cross the budget — so a lone trip is
        /// forgiven by the next success and a mod that keeps allocating within the committed envelope is bounded
        /// by the per-call step/time budgets instead. Defaults to
        /// <see cref="LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget"/>.
        /// </param>
        /// <param name="maxErrorsBeforeQuarantine">
        /// Consecutive-error streak (reset by any success) at which a mod is quarantined — dispatch
        /// suspended, mod kept loaded and repairable. Defaults to
        /// <see cref="DefaultMaxErrorsBeforeQuarantine"/>; clamped to at least 1.
        /// </param>
        /// <param name="logicSlots">
        /// Optional shared logic-slot surface. When supplied, the runtime clears a mod's slot
        /// overrides on unload/reload/quarantine (<see cref="ModTearingDown"/>) and records override
        /// failures in the same diagnostics channel as handler errors, attributed to the owning mod.
        /// </param>
        public LuaCsModRuntime(
            Action<IScriptFunctionRegistry, LuaCapabilities, string> gameplayBindings = null,
            ILuaModStore store = null,
            ILog log = null,
            int handlerTimeoutMs = DefaultHandlerTimeoutMs,
            long handlerMaxSteps = DefaultHandlerMaxSteps,
            ILuaModSourceStore sourceStore = null,
            bool autoPersistMods = true,
            ILuaScriptVersionStore versionStore = null,
            ILuaTransactionScope transactionScope = null,
            long handlerMaxAllocatedBytes = LuaCsExecutionGuard.DefaultMaxAllocatedBytesBudget,
            IScriptEngine engine = null,
            int maxErrorsBeforeQuarantine = DefaultMaxErrorsBeforeQuarantine,
            LuaCsLogicSlots logicSlots = null)
        {
            _gameplayBindings = gameplayBindings;
            _store = store;
            _log = log;
            _sourceStore = sourceStore ?? NullLuaModSourceStore.Instance;
            _versionStore = versionStore ?? new NullLuaScriptVersionStore();
            _autoPersistMods = autoPersistMods;
            _transactionScope = transactionScope;
            MaxErrorsBeforeQuarantine = Math.Max(1, maxErrorsBeforeQuarantine);
            _logicSlots = logicSlots;
            if (_logicSlots != null)
            {
                // WHY: A failing logic_define override is a MOD failure, not a host detail: routing it
                // into the handler-error channel makes it visible to diagnostics/auto-repair instead of
                // the old silent revert-to-vanilla.
                _logicSlots.OverrideFailed += OnLogicSlotOverrideFailed;
            }

            // WHY: The factory is the composition root that wires the engine; the default here only keeps
            // direct construction (tests, fixtures) working without an explicit engine.
            _engine = engine ?? new LuaCsScriptEngine();
            _marshaller = _engine.Marshaller;
            _handlerGuard = _engine.CreateGuard(
                new ExecutionBudget(handlerTimeoutMs, handlerMaxSteps, handlerMaxAllocatedBytes));
        }

        /// <summary>The <see cref="ILuaScriptVersionStore"/> key for a mod's revision history.</summary>
        private static string VersionKey(string modId)
        {
            return VersionKeyPrefix + modId;
        }

        /// <summary>Snapshot of all loaded mods.</summary>
        public IReadOnlyList<LuaModInfo> ListMods()
        {
            List<LuaModInfo> result = new();
            lock (_gate)
            {
                foreach (Mod mod in _mods.Values)
                {
                    result.Add(new LuaModInfo
                    {
                        Id = mod.Id,
                        Capabilities = mod.Caps,
                        HandlerCount = mod.HandlerCount,
                        TimerCount = mod.Timers.Count,
                        ErrorCount = mod.ErrorCount,
                        LogReports = mod.LogReports,
                        LoadedAtUtc = mod.LoadedAtUtc,
                        Quarantined = mod.Quarantined
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Returns the Lua source of a loaded mod (the exact chunk passed to
        /// <see cref="LoadMod"/>/<see cref="ReloadMod"/>). False when no mod with this id is loaded.
        /// </summary>
        public bool TryGetModSource(string id, out string source)
        {
            lock (_gate)
            {
                if (_mods.TryGetValue(Normalize(id), out Mod mod))
                {
                    source = mod.Source;
                    return true;
                }
            }

            source = "";
            return false;
        }

        /// <summary>Returns whether <c>report()</c> output is logged for a loaded mod.</summary>
        public bool GetModReportLoggingEnabled(string id)
        {
            lock (_gate)
            {
                return _mods.TryGetValue(Normalize(id), out Mod mod) && mod.LogReports;
            }
        }

        /// <summary>Enables or disables <c>report()</c> output for a loaded mod.</summary>
        public bool SetModReportLoggingEnabled(string id, bool enabled)
        {
            lock (_gate)
            {
                if (!_mods.TryGetValue(Normalize(id), out Mod mod))
                {
                    return false;
                }

                mod.LogReports = enabled;
                return true;
            }
        }

        /// <summary>True when a mod with this id is currently loaded.</summary>
        public bool IsLoaded(string id)
        {
            lock (_gate)
            {
                return _mods.ContainsKey(Normalize(id));
            }
        }

        /// <summary>
        /// Loads a mod: creates a sandboxed Lua-CSharp state with the (optional) gameplay bindings plus
        /// mod-core APIs and runs the chunk (which registers its hooks). Throws on invalid input,
        /// duplicate id, mod-count limit, or script error — nothing is left registered when the load
        /// fails.
        /// </summary>
        /// <param name="persistToStore">
        /// When true (default), the mod's source and manifest are written to the source store. Rehydration
        /// and import pass false so loading a mod with masked runtime capabilities never overwrites the
        /// declared capabilities already recorded in the store.
        /// </param>
        public void LoadMod(
            string id,
            string luaCode,
            LuaCapabilities capabilities = LuaCapabilities.All,
            bool persistToStore = true)
        {
            string modId = Normalize(id);
            if (modId.Length == 0)
            {
                throw new ArgumentException("Mod id is required.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(luaCode))
            {
                throw new ArgumentException("Mod code is required.", nameof(luaCode));
            }

            lock (_gate)
            {
                if (_mods.ContainsKey(modId))
                {
                    throw new InvalidOperationException($"Mod '{modId}' is already loaded. Use ReloadMod.");
                }

                if (_mods.Count >= DefaultMaxMods)
                {
                    throw new InvalidOperationException($"Mod limit reached ({DefaultMaxMods}).");
                }
            }

            Mod mod = BuildMod(modId, luaCode, capabilities);

            lock (_gate)
            {
                if (_mods.ContainsKey(modId))
                {
                    throw new InvalidOperationException($"Mod '{modId}' was loaded concurrently.");
                }

                _mods[modId] = mod;
            }

            _log?.Info($"[LuaCsModRuntime] Mod '{modId}' loaded (caps={capabilities}).");

            // WHY: Record the revision before persisting so PersistMod can stamp the manifest Version from the
            // revision count. The version store seeds the original on the first record and dedups identical
            // source, so a rehydrate (which replays the stored current source) does not create a spurious
            // entry. Recording is independent of persistToStore: a masked rehydrate/import still wants its
            // history seeded.
            RecordRevision(modId, luaCode);
            if (persistToStore)
            {
                PersistMod(modId, luaCode, capabilities);
            }

            RaiseModSourceLoaded(modId, luaCode, capabilities);
        }

        /// <summary>
        /// Creates the sandboxed state with capability-scoped gameplay bindings plus mod-core APIs and
        /// runs the chunk (hook registration happens there). Errors propagate to the caller and the mod
        /// is never added, so a failed build leaves no handlers behind.
        /// </summary>
        private Mod BuildMod(string modId, string luaCode, LuaCapabilities capabilities)
        {
            Mod mod = new()
            {
                Id = modId,
                Source = luaCode,
                Caps = capabilities,
                LoadedAtUtc = DateTime.UtcNow
            };

            // WHY: Downlevel Luau -> Lua 5.2 BEFORE the VM compiles the chunk so mods may use Luau
            // syntax (compound assignment, continue, string interpolation, if-expressions, type
            // annotations). Keyed by the mod id so any surfaced diagnostic maps to the right source; a
            // downlevel Error throws out of the load (never a silent raw fallback). mod.Source keeps the
            // ORIGINAL author text — get_source/versions round-trip the Luau the user wrote.
            string compileSource = LuauSourceGate.ToLua52(luaCode, modId);

            IScriptFunctionRegistry registry = _engine.CreateFunctionRegistry();
            RegisterGameplayBindings(registry, capabilities, modId);
            RegisterModApis(registry, mod);

            // WHY: Create the state BEFORE running the chunk; the mod-core callbacks capture `mod` and read
            // mod.State (set here) only when they later run, so self-referential cross-mod calls made
            // during load resolve correctly.
            mod.State = _engine.CreateState();
            registry.ApplyTo(mod.State);

            // WHY: Run the load chunk on its own transaction frame (mirroring the MoonSharp runtime's
            // per-run reset) so a transaction left open by a failing load is discarded with the frame and
            // cannot bleed into later scripts — and a transaction leaked elsewhere cannot swallow this
            // chunk's world commands.
            PushTransactionScope();
            try
            {
                _engine.RunChunk(mod.State, compileSource);
            }
            finally
            {
                PopTransactionScope();
            }

            return mod;
        }

        /// <summary>
        /// SEAM — <c>// TODO(migration): connect ported world/unity gameplay bindings here.</c>
        /// The MoonSharp runtime registers ~36 capability-scoped world/unity APIs via
        /// <c>IGameLuaRuntimeBindings.RegisterGameplayApis(LuaApiRegistry)</c> (optionally scoped by
        /// <c>ICapabilityScopedLuaBindings</c>). Those heavy bindings are NOT ported to Lua-CSharp in
        /// this pass. Until a ported binding provider exists, a host may inject an
        /// <see cref="Action{IScriptFunctionRegistry, LuaCapabilities}"/> that registers gameplay APIs on the
        /// per-mod <see cref="IScriptFunctionRegistry"/>, scoped to <paramref name="capabilities"/>. The
        /// callback is responsible for its own fail-closed capability trimming.
        /// </summary>
        private void RegisterGameplayBindings(IScriptFunctionRegistry registry, LuaCapabilities capabilities,
            string ownerModId)
        {
            if (_gameplayBindings == null || capabilities == LuaCapabilities.None)
            {
                return;
            }

            // TODO(migration): connect ported world/unity gameplay bindings here.
            _gameplayBindings(registry, capabilities, ownerModId);
        }

        /// <summary>Unloads a mod and drops its handlers/timers/queued events.</summary>
        public bool UnloadMod(string id)
        {
            string modId = Normalize(id);
            string source;
            LuaCapabilities caps;
            lock (_gate)
            {
                if (!_mods.TryGetValue(modId, out Mod mod))
                {
                    return false;
                }

                source = mod.Source;
                caps = mod.Caps;
                _mods.Remove(modId);
            }

            TeardownModEffects(modId, LuaModTeardownReason.Unload);
            _log?.Info($"[LuaCsModRuntime] Mod '{modId}' unloaded.");

            // WHY: Keep the persisted package but mark it dormant so it does not auto-reload next start; the
            // source is not lost (use ForgetMod to delete it). Best-effort: a store failure must not
            // break unloading.
            if (_autoPersistMods)
            {
                try
                {
                    _sourceStore.SetActive(modId, false);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] Source store SetActive('{modId}', false) failed: {ex}");
                }
            }

            RaiseModSourceUnloaded(modId, source, caps);
            return true;
        }

        /// <summary>
        /// Replaces a loaded mod with new code, keeping its capability tier. The new chunk is built and
        /// run first; if it fails, the old mod stays loaded and untouched (including its quarantine
        /// state). On success the old instance's runtime effects are torn down before the swap
        /// (<see cref="ModTearingDown"/> with <see cref="LuaModTeardownReason.Reload"/> — its logic-slot
        /// overrides are cleared while the replacement chunk's own <c>logic_define</c> calls are kept),
        /// and the replacement starts with a zero error streak and no quarantine, so reloading is THE
        /// way to bring a quarantined mod back to life.
        /// </summary>
        public void ReloadMod(string id, string luaCode)
        {
            string modId = Normalize(id);
            if (string.IsNullOrWhiteSpace(luaCode))
            {
                throw new ArgumentException("Mod code is required.", nameof(luaCode));
            }

            LuaCapabilities caps;
            lock (_gate)
            {
                if (!_mods.TryGetValue(modId, out Mod existing))
                {
                    throw new InvalidOperationException($"Mod '{modId}' is not loaded.");
                }

                caps = existing.Caps;
            }

            Mod replacement = BuildMod(modId, luaCode, caps);

            // WHY: Teardown BEFORE the swap so the old instance's effects (its logic-slot overrides)
            // are gone by the time the replacement is live — the old formula must never be invoked
            // after the new load. The replacement's state is excluded: its load chunk already ran in
            // BuildMod and may have re-defined slots, and those fresh defines must survive.
            TeardownModEffects(modId, LuaModTeardownReason.Reload, replacement.State);

            lock (_gate)
            {
                _mods[modId] = replacement;
            }

            _log?.Info($"[LuaCsModRuntime] Mod '{modId}' reloaded (caps={caps}).");
            RecordRevision(modId, luaCode);
            PersistMod(modId, luaCode, caps);
            RaiseModSourceLoaded(modId, luaCode, caps);
        }

        /// <summary>
        /// Records <paramref name="luaCode"/> as a new revision of the mod in the version store. Best-effort:
        /// a store failure is logged, never thrown out of a load/reload. <c>SeedOriginal</c> establishes the
        /// baseline (revision 0) on the first record; <c>RecordSuccessfulExecution</c> appends a new revision
        /// only when the source actually changed, so a no-op reload does not grow the history.
        /// </summary>
        private void RecordRevision(string modId, string luaCode)
        {
            try
            {
                string key = VersionKey(modId);
                _versionStore.SeedOriginal(key, luaCode);
                _versionStore.RecordSuccessfulExecution(key, luaCode);
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Version store record for '{modId}' failed: {ex}");
            }
        }

        /// <summary>
        /// Returns the recorded revision history for a mod (revision 0 = original), newest last, or an empty
        /// list when the mod has no tracked history (no version store, or never loaded through one).
        /// </summary>
        public IReadOnlyList<LuaScriptRevision> ListModVersions(string id)
        {
            string modId = Normalize(id);
            try
            {
                if (_versionStore.TryGetSnapshot(VersionKey(modId), out LuaScriptVersionRecord snapshot) &&
                    snapshot != null)
                {
                    return snapshot.History;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Version store snapshot for '{modId}' failed: {ex}");
            }

            return Array.Empty<LuaScriptRevision>();
        }

        /// <summary>
        /// Rolls a mod back to a recorded revision. When the mod is currently loaded it is reloaded from that
        /// revision's source; the reload appends the restored source as the new current revision (a
        /// non-destructive revert — the history is an audit trail, not rewound) and re-persists the source
        /// store and manifest Version. When the mod is not loaded the version store is rewound to that revision
        /// instead (truncating later revisions), so a future load starts from the chosen point. Sets
        /// <paramref name="restoredSource"/> and returns true on success; returns false when the mod has no such
        /// revision. Throws if the restored source fails to reload (the live mod stays untouched, exactly like
        /// <see cref="ReloadMod"/>).
        /// </summary>
        public bool TryRevertMod(string id, int revisionIndex, out string restoredSource)
        {
            restoredSource = null;
            string modId = Normalize(id);
            if (revisionIndex < 0)
            {
                return false;
            }

            LuaScriptVersionRecord snapshot;
            try
            {
                if (!_versionStore.TryGetSnapshot(VersionKey(modId), out snapshot) || snapshot == null)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Version store snapshot for '{modId}' failed: {ex}");
                return false;
            }

            // WHY: Revision indices are stable sequence numbers assigned by the version store, not positions in
            // History: the store's retention policy can evict middle revisions, leaving gaps, so the
            // requested index must be searched rather than used to index the list directly.
            LuaScriptRevision revision = FindRevisionByIndex(snapshot.History, revisionIndex);
            if (revision == null)
            {
                return false;
            }

            string source = revision.Source ?? "";
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            // WHY: Reload first (it can throw on a bad revision, leaving the live mod untouched), then truncate the
            // version history to the chosen revision so a future revert references a clean lineage. Reload
            // re-records the restored source as the new current revision and re-persists the source store.
            if (IsLoaded(modId))
            {
                ReloadMod(modId, source);
            }
            else
            {
                try
                {
                    _versionStore.ResetToRevision(VersionKey(modId), revisionIndex);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] Version store revert for '{modId}' failed: {ex}");
                    return false;
                }
            }

            restoredSource = source;
            return true;
        }

        /// <summary>Finds the revision with the given stable <see cref="LuaScriptRevision.Index"/>, or null if it was evicted or never recorded.</summary>
        private static LuaScriptRevision FindRevisionByIndex(IReadOnlyList<LuaScriptRevision> history,
            int revisionIndex)
        {
            for (int i = 0; i < history.Count; i++)
            {
                if (history[i].Index == revisionIndex)
                {
                    return history[i];
                }
            }

            return null;
        }

        /// <summary>Queues a game event for delivery to every mod's <c>hooks_on</c> handlers on the next <see cref="Tick"/>.</summary>
        public void EmitEvent(string name, string payload = "")
        {
            string evt = Normalize(name);
            if (evt.Length == 0)
            {
                return;
            }

            lock (_gate)
            {
                foreach (Mod mod in _mods.Values)
                {
                    EnqueueLocked(mod, evt, payload ?? "");
                }
            }
        }

        /// <summary>
        /// Advances timers and dispatches queued events. Call once per frame from the host (main
        /// thread); every handler call is individually instruction/time guarded.
        /// </summary>
        public void Tick(double deltaSeconds)
        {
            if (deltaSeconds < 0d || double.IsNaN(deltaSeconds))
            {
                return;
            }

            lock (_gate)
            {
                if (_mods.Count == 0)
                {
                    return;
                }

                _tickScratch.Clear();
                foreach (Mod mod in _mods.Values)
                {
                    // WHY: Quarantined mods stay loaded/addressable but must not run: no timers, no
                    // handler dispatch, until a reload replaces them with a fresh instance.
                    if (!mod.Quarantined)
                    {
                        _tickScratch.Add(mod);
                    }
                }

                if (_tickScratch.Count == 0)
                {
                    return;
                }
            }

            // WHY: Timers always run (bounded to one fire per timer per tick); they are not charged against
            // the global event budget, so they run for every mod in iteration order.
            for (int i = 0; i < _tickScratch.Count; i++)
            {
                TickTimers(_tickScratch[i], deltaSeconds);
            }

            // WHY: Only event dispatch is charged against the global budget. The start index rotates
            // round-robin every tick so the budget is shared fairly across all mods over successive
            // ticks; mods not reached keep their queued events for later ticks (no drops).
            int count = _tickScratch.Count;
            int start = count > 0 ? (_dispatchRotation % count + count) % count : 0;
            _dispatchRotation++;

            int dispatchedThisTick = 0;
            for (int n = 0; n < count; n++)
            {
                Mod mod = _tickScratch[(start + n) % count];

                if (dispatchedThisTick < DefaultMaxEventsDispatchedPerTickGlobal)
                {
                    try
                    {
                        dispatchedThisTick += DispatchPendingEvents(mod, dispatchedThisTick);
                    }
                    catch (Exception ex)
                    {
                        // WHY: Defence in depth: a single mod's dispatch failure must never abort the whole
                        // per-frame tick for the other mods.
                        mod.ErrorCount++;
                        _log?.Error($"[LuaCsModRuntime] Mod '{mod.Id}' event dispatch failed: {ex}");
                    }
                }

                QuarantineIfExhausted(mod);
            }
        }

        /// <summary>
        /// Quarantines a mod whose consecutive-error streak reached <see cref="MaxErrorsBeforeQuarantine"/>:
        /// dispatch is suspended and its logic-slot overrides revert to vanilla, but the mod stays in the
        /// registry so diagnostics still see it and <see cref="ReloadMod"/> can repair it at any time.
        /// </summary>
        private void QuarantineIfExhausted(Mod mod)
        {
            if (mod.Quarantined || mod.ErrorCount < MaxErrorsBeforeQuarantine)
            {
                return;
            }

            lock (_gate)
            {
                // WHY: `mod` comes from the tick snapshot and may be STALE: a repair's ReloadMod can land
                // mid-tick (e.g. from a ModHandlerErrored subscriber) and swap the registry entry. Only
                // the still-live instance may be quarantined — quarantining by id from the old object's
                // error streak would suspend the freshly repaired mod.
                if (!_mods.TryGetValue(mod.Id, out Mod live) || !ReferenceEquals(live, mod))
                {
                    return;
                }

                mod.Quarantined = true;
            }

            _log?.Warn(
                $"[LuaCsModRuntime] Mod '{mod.Id}' quarantined after {mod.ErrorCount} consecutive handler " +
                "errors: dispatch suspended, mod kept loaded; reload it to clear the quarantine.");

            TeardownModEffects(mod.Id, LuaModTeardownReason.Quarantine);
            RaiseModQuarantined(mod.Id, mod.ErrorCount);
        }

        /// <summary>
        /// Central teardown of one mod instance's runtime side effects, shared by unload, reload
        /// (before the swap; <paramref name="keepState"/> excludes the replacement's fresh defines) and
        /// quarantine entry: clears the mod's logic-slot overrides (fail back to the vanilla formula)
        /// and raises <see cref="ModTearingDown"/> so future subsystems can release the mod's effects
        /// at the same point. Best-effort: a failing slot surface must not break the lifecycle path.
        /// </summary>
        private void TeardownModEffects(string modId, LuaModTeardownReason reason, IScriptState keepState = null)
        {
            if (_logicSlots != null)
            {
                try
                {
                    int cleared = _logicSlots.ClearOwnedBy(modId, keepState);
                    if (cleared > 0)
                    {
                        _log?.Info(
                            $"[LuaCsModRuntime] Cleared {cleared} logic-slot override(s) of mod '{modId}' ({reason}).");
                    }
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] Clearing logic-slot overrides of '{modId}' failed: {ex}");
                }
            }

            RaiseModTearingDown(modId, reason);
        }

        /// <summary>
        /// Routes a logic-slot override failure (already reset to vanilla by <see cref="LuaCsLogicSlots"/>)
        /// into the mod's handler-error channel: it charges the owning mod's consecutive-error streak and
        /// is recorded/raised like any hook failure, so <c>diagnostics</c>/auto-repair see WHICH mod's
        /// formula broke instead of a silent revert.
        /// </summary>
        private void OnLogicSlotOverrideFailed(string ownerModId, string slot, string error)
        {
            string modId = Normalize(ownerModId);
            if (modId.Length == 0)
            {
                return;
            }

            int streak;
            lock (_gate)
            {
                if (_mods.TryGetValue(modId, out Mod mod))
                {
                    mod.ErrorCount++;
                    streak = mod.ErrorCount;
                }
                else
                {
                    streak = 1;
                }
            }

            string message = $"logic slot '{slot}' override failed and was reset to vanilla: {error}";
            RecordHandlerError(modId, message, streak);
            RaiseModHandlerErrored(modId, message, streak);
        }

        private void TickTimers(Mod mod, double dt)
        {
            for (int i = 0; i < mod.Timers.Count; i++)
            {
                TimerEntry timer = mod.Timers[i];
                timer.DueIn -= dt;
                if (timer.DueIn > 0d)
                {
                    continue;
                }

                // WHY: One invocation per tick maximum — a long hitch must not burst-fire a timer.
                timer.DueIn = timer.IntervalSeconds;
                InvokeGuarded(mod, timer.Fn);
            }
        }

        /// <summary>
        /// Dispatches this mod's queued events, honouring both the per-mod cap and the shared global
        /// budget. Returns the number of invocations this mod dispatched; surplus events stay queued
        /// and are carried over to the next tick (no-drop contract).
        /// </summary>
        private int DispatchPendingEvents(Mod mod, int alreadyDispatchedThisTick)
        {
            int globalRemaining = DefaultMaxEventsDispatchedPerTickGlobal - alreadyDispatchedThisTick;
            int limit = Math.Min(DefaultMaxEventsDispatchedPerTick, globalRemaining);
            int dispatched = 0;
            while (dispatched < limit)
            {
                KeyValuePair<string, string> evt;
                object[] handlerSnapshot;
                lock (_gate)
                {
                    if (mod.Pending.Count == 0)
                    {
                        return dispatched;
                    }

                    evt = mod.Pending.Peek();

                    // WHY: Snapshot the handler list under the gate: a dispatched handler may call hooks_on()
                    // for the same event, mutating mod.Handlers; enumerating the live list would then
                    // throw out of the (unguarded) tick.
                    handlerSnapshot = mod.Handlers.TryGetValue(evt.Key, out List<object> handlers)
                        ? handlers.ToArray()
                        : Array.Empty<object>();

                    // WHY: No-drop contract: only dequeue when the remaining budget can run every handler of
                    // this event. Exception: an event whose own handler count exceeds the whole budget
                    // would starve forever, so when nothing has run yet for this mod we dispatch it in
                    // full (bounded by the per-mod handler cap) to guarantee progress.
                    if (handlerSnapshot.Length > limit - dispatched && dispatched > 0)
                    {
                        return dispatched;
                    }

                    mod.Pending.Dequeue();
                }

                foreach (object fn in handlerSnapshot)
                {
                    InvokeGuarded(mod, fn, evt.Key, evt.Value);
                    dispatched++;
                }
            }

            return dispatched;
        }

        private void InvokeGuarded(Mod mod, object fn, params object[] args)
        {
            // WHY: Push an isolated transaction frame around this call (mirroring the MoonSharp runtime and
            // LuaCsGameToolExecutor) so a transaction opened inside one invocation is discarded with the
            // frame on exit and cannot leak into the next handler/timer/tick — and a nested mods_call runs
            // on its OWN frame instead of corrupting this call's still-open transaction.
            PushTransactionScope();
            try
            {
                _handlerGuard.Invoke(mod.State, fn, CancellationToken.None, args);

                // WHY: "MaxErrorsBeforeQuarantine failures in a row": a successful call forgives past
                // errors, so rare sporadic failures over a long lifetime do not quarantine the mod.
                mod.ErrorCount = 0;
            }
            catch (Exception ex)
            {
                // WHY: An allocation-budget trip is charged to the same consecutive-error streak as any other
                // failure — a success resets it, so a mod is only quarantined after MaxErrorsBeforeQuarantine
                // failures IN A ROW. This is safe despite the budget reading the shared process heap (which can
                // trip a blameless mod on unrelated growth) because a memory trip is effectively a
                // ONCE-PER-LIFETIME event: GC.GetTotalMemory reports the COMMITTED heap high-water mark, so the
                // FIRST oversized allocation grows the heap and trips, but every later call reuses that committed
                // space and its per-call delta no longer crosses the budget (verified empirically — a mod bombing
                // every tick trips ~once, not repeatedly, even with a forced GC between calls). So a lone noise
                // trip is forgiven by the next success and never quarantines a healthy mod, while the per-call
                // step/time budgets — not this streak — are what bound a mod that keeps allocating within the
                // committed envelope. Classified by TYPE (see IsMemoryBudgetTrip) for the log label only; a mod
                // cannot forge the marker in its own error text to change how it is charged (all failures charge
                // alike).
                bool memoryTrip = ScriptExecutionErrors.IsMemoryBudgetTrip(ex);
                mod.ErrorCount++;

                _log?.Error(
                    $"[LuaCsModRuntime] Mod '{mod.Id}' handler failed " +
                    $"({(memoryTrip ? "memory-budget trip" : "error")} {mod.ErrorCount}/{MaxErrorsBeforeQuarantine}): {ex}");

                string message = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (message.Length == 0)
                {
                    message = ex.GetType().Name;
                }

                // WHY: Buffer the failure so the agent can poll it next turn, independent of any host-side
                // ModHandlerErrored subscriber.
                RecordHandlerError(mod.Id, message, mod.ErrorCount);

                // WHY: Surface the runtime failure so hosts can drive auto-repair. Fired outside the gate; a
                // throwing subscriber must not derail the tick.
                RaiseModHandlerErrored(mod.Id, message, mod.ErrorCount);
            }
            finally
            {
                PopTransactionScope();
            }
        }

        /// <summary>
        /// Pushes an isolated world/data transaction frame on the shared gameplay-binding scope for one
        /// guarded run. Best-effort: a throwing scope must not derail the load/tick path.
        /// </summary>
        private void PushTransactionScope()
        {
            if (_transactionScope == null)
            {
                return;
            }

            try
            {
                _transactionScope.PushTransactionScope();
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] ILuaTransactionScope.PushTransactionScope() failed: {ex}");
            }
        }

        /// <summary>
        /// Pops the transaction frame pushed by <see cref="PushTransactionScope"/>, discarding any
        /// unfinished transaction it holds. Best-effort: it runs inside finally blocks on the load/tick
        /// path, so a throwing scope must not derail them.
        /// </summary>
        private void PopTransactionScope()
        {
            if (_transactionScope == null)
            {
                return;
            }

            try
            {
                _transactionScope.PopTransactionScope();
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] ILuaTransactionScope.PopTransactionScope() failed: {ex}");
            }
        }

        // WHY: Per-subscriber isolated event raises. A host UI/telemetry listener throwing must never make a
        // healthy mod load/reload/unload/report/error notification appear failed to the caller: every
        // subscriber on the invocation list is called independently, and a subscriber's exception is
        // logged and swallowed rather than propagated or allowed to skip the remaining subscribers.

        private void RaiseModSourceLoaded(string modId, string source, LuaCapabilities caps)
        {
            Action<string, string, LuaCapabilities> handler = ModSourceLoaded;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, string, LuaCapabilities> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, source, caps);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModSourceLoaded handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RaiseModSourceUnloaded(string modId, string source, LuaCapabilities caps)
        {
            Action<string, string, LuaCapabilities> handler = ModSourceUnloaded;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, string, LuaCapabilities> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, source, caps);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModSourceUnloaded handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RaiseModQuarantined(string modId, int errorCount)
        {
            Action<string, int> handler = ModQuarantined;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, int> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, errorCount);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModQuarantined handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RaiseModTearingDown(string modId, LuaModTeardownReason reason)
        {
            Action<string, LuaModTeardownReason> handler = ModTearingDown;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, LuaModTeardownReason> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, reason);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModTearingDown handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RaiseModHandlerErrored(string modId, string message, int consecutiveErrorCount)
        {
            Action<string, string, int> handler = ModHandlerErrored;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, string, int> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, message, consecutiveErrorCount);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModHandlerErrored handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RaiseModReportEmitted(string modId, string message)
        {
            Action<string, string> handler = ModReportEmitted;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, string> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, message);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModReportEmitted handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RaiseModEventEmitted(string modId, string evt, string payload)
        {
            Action<string, string, string> handler = ModEventEmitted;
            if (handler == null)
            {
                return;
            }

            foreach (Action<string, string, string> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(modId, evt, payload);
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] [subscriber] ModEventEmitted handler for '{modId}' threw: {ex}");
                }
            }
        }

        private void RegisterModApis(IScriptFunctionRegistry registry, Mod mod)
        {
            registry.Register("mod_id", new Func<string>(() => mod.Id));

            registry.RegisterVarArgs("hooks_on", call =>
            {
                string name = Normalize(call.GetString(0));
                object fn = ReadFunction(call, 1);
                if (name.Length == 0 || fn == null)
                {
                    throw new ArgumentException("hooks_on: event name and function are required.");
                }

                // WHY: LLM-written mods routinely register hooks_on("tick"/"update"/"frame", fn) expecting a
                // per-frame callback, but hooks_on only receives NAMED events and nothing emits those —
                // the handler would sit dead forever. Route the intuitive spelling to the timer
                // machinery at the shortest allowed interval instead.
                if (name is "tick" or "update" or "frame")
                {
                    if (mod.Timers.Count >= DefaultMaxTimersPerMod)
                    {
                        throw new InvalidOperationException(
                            $"hooks_on('{name}'): timer limit reached ({DefaultMaxTimersPerMod}).");
                    }

                    mod.Timers.Add(new TimerEntry
                    {
                        IntervalSeconds = MinTimerIntervalSeconds,
                        DueIn = MinTimerIntervalSeconds,
                        Fn = fn
                    });
                    return ScriptCallResult.Return(true);
                }

                if (mod.HandlerCount >= DefaultMaxHandlersPerMod)
                {
                    throw new InvalidOperationException(
                        $"hooks_on: handler limit reached ({DefaultMaxHandlersPerMod}).");
                }

                if (!mod.Handlers.TryGetValue(name, out List<object> list))
                {
                    list = new List<object>();
                    mod.Handlers[name] = list;
                }

                list.Add(fn);
                mod.HandlerCount++;
                return ScriptCallResult.Return(true);
            });

            registry.RegisterVarArgs("hooks_every", call =>
            {
                double seconds = call.GetNumber(0);
                object fn = ReadFunction(call, 1);
                if (fn == null || double.IsNaN(seconds) || double.IsInfinity(seconds) ||
                    seconds < MinTimerIntervalSeconds)
                {
                    throw new ArgumentException(
                        $"hooks_every: interval must be >= {MinTimerIntervalSeconds}s and fn required.");
                }

                if (mod.Timers.Count >= DefaultMaxTimersPerMod)
                {
                    throw new InvalidOperationException(
                        $"hooks_every: timer limit reached ({DefaultMaxTimersPerMod}).");
                }

                mod.Timers.Add(new TimerEntry { IntervalSeconds = seconds, DueIn = seconds, Fn = fn });
                return ScriptCallResult.Return(true);
            });

            registry.Register("events_emit", new Func<string, string, bool>((evt, payload) =>
            {
                string name = Normalize(evt);
                if (name.Length == 0)
                {
                    throw new ArgumentException("events_emit: event name is required.");
                }

                EmitFromMod(mod, name, payload ?? "");
                return true;
            }));

            registry.RegisterVarArgs("mods_export", call =>
            {
                string exportName = Normalize(call.GetString(0));
                if (exportName.Length == 0)
                {
                    throw new ArgumentException("mods_export: name is required.");
                }

                object value = call.GetArgument(1);
                lock (_gate)
                {
                    if (!mod.Exports.ContainsKey(exportName) && mod.Exports.Count >= DefaultMaxExportsPerMod)
                    {
                        throw new InvalidOperationException(
                            $"mods_export: export limit reached ({DefaultMaxExportsPerMod}).");
                    }

                    mod.Exports[exportName] = value;
                }

                return ScriptCallResult.Return(new object[] { null });
            });

            registry.RegisterVarArgs("mods_get", call =>
            {
                string targetId = call.GetString(0);
                string name = call.GetString(1);
                object export = FindExport(targetId, name, out Mod _);
                if (_marshaller.GetKind(export) == ScriptValueKind.Function)
                {
                    throw new ArgumentException(
                        $"mods_get: '{Normalize(name)}' of mod '{Normalize(targetId)}' is a function - use mods_call.");
                }

                // WHY: Marshal by value: cross-mod reads copy plain data only (no functions/closures/live
                // refs), so no mod can mutate another's state behind its back — the multiplayer-
                // determinism rule.
                return ScriptCallResult.Return(
                    _marshaller.FromPortable(_marshaller.ToPortable(export, CrossModTableDepth)));
            });

            registry.RegisterVarArgs("mods_call", call =>
            {
                string targetId = call.GetString(0);
                string name = call.GetString(1);
                object export = FindExport(targetId, name, out Mod target);
                if (_marshaller.GetKind(export) != ScriptValueKind.Function)
                {
                    throw new ArgumentException(
                        $"mods_call: '{Normalize(name)}' of mod '{Normalize(targetId)}' is not a function - use mods_get.");
                }

                if (_crossCallDepth >= MaxCrossCallDepth)
                {
                    throw new InvalidOperationException(
                        $"mods_call: cross-mod call depth limit reached ({MaxCrossCallDepth}) - break the cycle.");
                }

                int extra = Math.Max(0, call.ArgumentCount - 2);
                object[] marshalled = new object[extra];
                for (int i = 0; i < extra; i++)
                {
                    // WHY: Copy caller args into plain data, then rebuild them for the callee's state.
                    marshalled[i] = _marshaller.FromPortable(
                        _marshaller.ToPortable(call.GetArgument(i + 2), CrossModTableDepth));
                }

                _crossCallDepth++;

                // WHY: The callee runs on a DIFFERENT state but shares this runtime's single world
                // binding instance. Push an isolated transaction frame so the callee's
                // coreai_world_begin/commit operate on their OWN frame and cannot flush or clear the
                // caller's still-open transaction (the buffer-corruption bug); popped in finally so a
                // transaction the callee leaks is discarded rather than bleeding back into the caller.
                PushTransactionScope();
                try
                {
                    object[] results =
                        _handlerGuard.Invoke(target.State, export, CancellationToken.None, marshalled);
                    object first = results.Length > 0 ? results[0] : null;

                    // WHY: Marshal the result back into the caller's state by value.
                    return ScriptCallResult.Return(
                        _marshaller.FromPortable(_marshaller.ToPortable(first, CrossModTableDepth)));
                }
                finally
                {
                    PopTransactionScope();
                    _crossCallDepth--;
                }
            });

            registry.Register("mods_list_exports", new Func<string, List<string>>(targetId =>
            {
                lock (_gate)
                {
                    if (!_mods.TryGetValue(Normalize(targetId), out Mod target))
                    {
                        throw new ArgumentException($"mods_list_exports: mod '{Normalize(targetId)}' is not loaded.");
                    }

                    return new List<string>(target.Exports.Keys);
                }
            }));

            if (_store != null)
            {
                registry.Register("store_set", new Action<string, string>((key, value) =>
                {
                    string k = Normalize(key);
                    if (k.Length == 0)
                    {
                        throw new ArgumentException("store_set: key is required.");
                    }

                    _store.Set(mod.Id, k, value);
                }));

                registry.Register("store_get", new Func<string, string>(key =>
                    _store.Get(mod.Id, Normalize(key)) ?? ""));
            }

            registry.Register("report", new Action<string>(message =>
            {
                string text = message ?? "";

                // WHY: Buffered regardless of LogReports: the flag only gates the live event/log spam, not
                // this bounded history, so a Hub logs view can still show a muted mod's history.
                RecordReport(mod.Id, text);

                if (!mod.LogReports)
                {
                    return;
                }

                RaiseModReportEmitted(mod.Id, text);
            }));

            // WHY: print() inside a mod behaves like report(): same event pipeline, same LogReports mute,
            // same report buffer. Overrides the basic library's print on this mod's environment.
            registry.RegisterVarArgs("print", call =>
            {
                string[] parts = new string[call.ArgumentCount];
                for (int i = 0; i < call.ArgumentCount; i++)
                {
                    parts[i] = call.DescribeArgument(i);
                }

                string text = string.Join("\t", parts);
                RecordReport(mod.Id, text);

                if (mod.LogReports)
                {
                    RaiseModReportEmitted(mod.Id, text);
                }

                return ScriptCallResult.Empty;
            });
        }

        /// <summary>Resolves a mod's export or throws a descriptive error naming what is missing.</summary>
        private object FindExport(string targetId, string name, out Mod target)
        {
            string modId = Normalize(targetId);
            string exportName = Normalize(name);
            lock (_gate)
            {
                if (!_mods.TryGetValue(modId, out target))
                {
                    throw new ArgumentException($"mod '{modId}' is not loaded.");
                }

                // WHY: Quarantine must suspend ALL FOUR dispatch surfaces — handlers, timers, queued
                // events/logic_define overrides, AND cross-mod exports (mods_call/mods_get). Without
                // this guard a quarantined mod's export stays synchronously invokable: a broken export
                // that throws surfaces in the CALLER's InvokeGuarded catch and mis-charges the caller's
                // error streak, driving a healthy dependent mod into quarantine. Fail here so the error
                // is attributable to the quarantined target, not the caller.
                if (target.Quarantined)
                {
                    throw new InvalidOperationException(
                        $"mod '{modId}' is quarantined - its exports are suspended; reload it to clear the quarantine.");
                }

                if (!target.Exports.TryGetValue(exportName, out object export))
                {
                    throw new ArgumentException(
                        $"mod '{modId}' has no export '{exportName}' (mods_list_exports lists available names).");
                }

                return export;
            }
        }

        /// <summary>Reads a function-valued argument, or null when the argument is not a function.</summary>
        private static object ReadFunction(ScriptCallContext call, int index)
        {
            return call.GetKind(index) == ScriptValueKind.Function ? call.GetArgument(index) : null;
        }

        private void EmitFromMod(Mod sender, string evt, string payload)
        {
            // WHY: Deliver to every other mod's queue (no self-delivery: trivial infinite loops).
            lock (_gate)
            {
                foreach (Mod mod in _mods.Values)
                {
                    if (!ReferenceEquals(mod, sender))
                    {
                        EnqueueLocked(mod, evt, payload);
                    }
                }
            }

            RaiseModEventEmitted(sender.Id, evt, payload);
        }

        private void EnqueueLocked(Mod mod, string evt, string payload)
        {
            // WHY: Quarantine suspends the whole dispatch surface; queueing into a mod that will never
            // dispatch again (a reload swaps in a fresh instance with a fresh queue) is pure waste.
            if (mod.Quarantined)
            {
                return;
            }

            if (mod.Pending.Count >= DefaultMaxQueuedEventsPerMod)
            {
                // WHY: Drop oldest: a stalled mod must not grow its queue without bound.
                mod.Pending.Dequeue();
            }

            mod.Pending.Enqueue(new KeyValuePair<string, string>(evt, payload));
        }

        /// <summary>
        /// Unloads the mod (if loaded) <em>and</em> deletes its persisted package, so it does not
        /// rehydrate on a future start. Returns true when either an unload or a delete occurred.
        /// </summary>
        public bool ForgetMod(string id)
        {
            string modId = Normalize(id);
            bool wasLoaded = UnloadMod(modId);

            try
            {
                _sourceStore.Delete(modId);
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Source store Delete('{modId}') failed: {ex}");
                return wasLoaded;
            }

            return wasLoaded || modId.Length > 0;
        }

        /// <summary>
        /// Loads every stored mod whose manifest is <see cref="LuaModManifest.Active"/> and not already
        /// loaded. Each mod's persisted capability request is intersected with
        /// <paramref name="hostGrant"/> and (unless <paramref name="allowFull"/>) stripped of
        /// <see cref="LuaCapabilities.Full"/>, so a persisted or shared mod can never auto-acquire full
        /// reflection. Loads run in independent try/catch blocks so one bad package does not abort the
        /// rest. Returns the count successfully loaded.
        /// </summary>
        public int RehydrateFromStore(LuaCapabilities hostGrant, bool allowFull = false)
        {
            IReadOnlyList<LuaModManifest> manifests;
            try
            {
                manifests = _sourceStore.List() ?? Array.Empty<LuaModManifest>();
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Source store List() failed during rehydrate: {ex}");
                return 0;
            }

            int loaded = 0;
            foreach (LuaModManifest manifest in manifests)
            {
                if (manifest == null || !manifest.Active)
                {
                    continue;
                }

                string modId = Normalize(manifest.Id);
                if (modId.Length == 0 || IsLoaded(modId))
                {
                    continue;
                }

                try
                {
                    if (!_sourceStore.TryLoad(modId, out string source, out LuaModManifest stored) ||
                        string.IsNullOrWhiteSpace(source))
                    {
                        _log?.Warn($"[LuaCsModRuntime] Rehydrate skipped '{modId}': no source in store.");
                        continue;
                    }

                    string capsText = stored != null ? stored.Capabilities : manifest.Capabilities;
                    LuaCapabilities effectiveCaps = ApplyHostGrant(ParseCaps(capsText), hostGrant, allowFull);

                    // WHY: Load with the masked runtime tier but do NOT re-persist: the stored manifest already
                    // holds the mod's declared capabilities. Overwriting it with the masked tier would
                    // permanently strip Full from the store, so a later allowFull rehydrate could not
                    // restore it.
                    LoadMod(modId, source, effectiveCaps, false);
                    loaded++;
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] Rehydrate of mod '{modId}' failed: {ex}");
                }
            }

            return loaded;
        }

        /// <summary>
        /// Returns a shareable JSON bundle <c>{ "manifest": {...}, "source": "..." }</c> for a loaded or
        /// stored mod, or null when neither holds the id.
        /// </summary>
        public string ExportMod(string id)
        {
            string modId = Normalize(id);
            string source = null;
            LuaModManifest manifest = null;

            lock (_gate)
            {
                if (_mods.TryGetValue(modId, out Mod mod))
                {
                    source = mod.Source;
                    manifest = BuildManifest(modId, mod.Caps, true);
                }
            }

            if (source == null)
            {
                try
                {
                    if (_sourceStore.TryLoad(modId, out string storedSource, out LuaModManifest storedManifest))
                    {
                        source = storedSource;
                        manifest = storedManifest ?? BuildManifest(modId, LuaCapabilities.None, false);
                    }
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaCsModRuntime] Source store TryLoad('{modId}') failed during export: {ex}");
                }
            }

            if (source == null)
            {
                return null;
            }

            try
            {
                return JsonConvert.SerializeObject(new LuaModBundle { Manifest = manifest, Source = source });
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Export of mod '{modId}' failed: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Parses an <see cref="ExportMod"/> bundle and loads (plus persists) it. The bundle's capability
        /// request is intersected with <paramref name="hostGrant"/> and (unless
        /// <paramref name="allowFull"/>) stripped of <see cref="LuaCapabilities.Full"/>, so an imported
        /// mod can never auto-acquire full reflection. Returns false on malformed input, a missing/blank
        /// id or source, or a load failure.
        /// </summary>
        public bool ImportMod(string bundleJson, LuaCapabilities hostGrant, bool allowFull = false)
        {
            if (string.IsNullOrWhiteSpace(bundleJson))
            {
                return false;
            }

            LuaModBundle bundle;
            try
            {
                bundle = JsonConvert.DeserializeObject<LuaModBundle>(bundleJson);
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] ImportMod failed to parse bundle: {ex}");
                return false;
            }

            if (bundle == null || string.IsNullOrWhiteSpace(bundle.Source))
            {
                _log?.Warn("[LuaCsModRuntime] ImportMod rejected: missing source.");
                return false;
            }

            string modId = Normalize(bundle.Manifest != null ? bundle.Manifest.Id : "");
            if (modId.Length == 0)
            {
                _log?.Warn("[LuaCsModRuntime] ImportMod rejected: missing/blank mod id.");
                return false;
            }

            string capsText = bundle.Manifest != null ? bundle.Manifest.Capabilities : "";
            LuaCapabilities effectiveCaps = ApplyHostGrant(ParseCaps(capsText), hostGrant, allowFull);

            try
            {
                if (IsLoaded(modId))
                {
                    // WHY: Reloading an already-loaded mod only swaps its SOURCE; it deliberately KEEPS the
                    // mod's current capability tier (ReloadMod reuses existing.Caps) and cannot escalate it —
                    // an import can never raise a live mod's privileges from an untrusted bundle header. To
                    // CHANGE a loaded mod's tier the host must unload/forget it first, then re-import under the
                    // desired grant (the fresh-load branch below applies the host-masked tier).
                    ReloadMod(modId, bundle.Source);
                }
                else
                {
                    // WHY: Persist the HOST-MASKED effective capabilities, NOT the bundle's declared request.
                    // An untrusted shared bundle can DECLARE Full; if the store recorded that declared tier,
                    // a later restart's RehydrateFromStore run under a host-wide allowFull=true would re-grant
                    // Full to this specific mod that was imported WITHOUT it — defeating the import-time
                    // decision across a restart. The store must never hold more than the host granted here; a
                    // host that later wants Full for a not-yet-loaded mod re-imports it under allowFull=true.
                    LoadMod(modId, bundle.Source, effectiveCaps, false);
                    PersistMod(modId, bundle.Source, effectiveCaps);
                }

                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] ImportMod of '{modId}' failed: {ex}");
                return false;
            }
        }

        /// <summary>JSON shape of an export/import bundle: the manifest plus the raw Lua source.</summary>
        private sealed class LuaModBundle
        {
            [JsonProperty("manifest")]
            public LuaModManifest Manifest;

            [JsonProperty("source")]
            public string Source = "";
        }

        /// <summary>Best-effort persist of a mod's source + manifest; a store failure is logged, never thrown.</summary>
        private void PersistMod(string modId, string source, LuaCapabilities caps)
        {
            if (!_autoPersistMods)
            {
                return;
            }

            try
            {
                _sourceStore.Save(modId, source, BuildManifest(modId, caps, true));
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Source store Save('{modId}') failed: {ex}");
            }
        }

        /// <summary>
        /// Builds a manifest for the given mod with its capability set rendered as a string. The
        /// <see cref="LuaModManifest.Version"/> is auto-derived from the revision count tracked in the version
        /// store (number of recorded revisions; "1" for a freshly seeded mod, blank when no history exists), so
        /// each edit through load/reload advances the persisted and exported version without the caller managing it.
        /// </summary>
        private LuaModManifest BuildManifest(string id, LuaCapabilities caps, bool active)
        {
            return new LuaModManifest
            {
                Id = id,
                Name = id,
                Capabilities = caps.ToString(),
                Active = active,
                Version = CurrentVersionString(id)
            };
        }

        /// <summary>
        /// Renders the mod's current version as the count of recorded revisions (e.g. "3" after three distinct
        /// edits). Blank when the version store holds no history for the mod. Best-effort: a store failure
        /// yields a blank version rather than throwing.
        /// </summary>
        private string CurrentVersionString(string id)
        {
            try
            {
                if (_versionStore.TryGetSnapshot(VersionKey(Normalize(id)), out LuaScriptVersionRecord snapshot) &&
                    snapshot != null && snapshot.History.Count > 0)
                {
                    return snapshot.History.Count.ToString();
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaCsModRuntime] Version store version lookup for '{id}' failed: {ex}");
            }

            return "";
        }

        /// <summary>
        /// Intersects a mod's requested capabilities with the host grant and, unless
        /// <paramref name="allowFull"/>, clears <see cref="LuaCapabilities.Full"/>. Persisted and shared
        /// mods route through here so they can never escalate beyond what the host currently allows.
        /// </summary>
        private static LuaCapabilities ApplyHostGrant(LuaCapabilities requested, LuaCapabilities hostGrant,
            bool allowFull)
        {
            LuaCapabilities effective = requested & hostGrant;
            if (!allowFull)
            {
                effective &= ~LuaCapabilities.Full;
            }

            return effective;
        }

        /// <summary>
        /// Tolerantly parses a persisted capability string into <see cref="LuaCapabilities"/>. An empty
        /// or unparsable value yields <see cref="LuaCapabilities.None"/> (fail closed) and is logged, so
        /// a corrupt manifest grants no capabilities rather than defaulting open.
        /// </summary>
        private LuaCapabilities ParseCaps(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return LuaCapabilities.None;
            }

            if (Enum.TryParse(text.Trim(), true, out LuaCapabilities parsed))
            {
                return parsed;
            }

            _log?.Warn($"[LuaCsModRuntime] Unrecognized capability string '{text}'; defaulting to None.");
            return LuaCapabilities.None;
        }

        /// <summary>
        /// Appends a Tick-time handler failure to the bounded recent-errors buffer, dropping the oldest
        /// entry when full.
        /// </summary>
        private void RecordHandlerError(string modId, string message, int consecutiveCount)
        {
            LuaModHandlerError entry = new()
            {
                ModId = modId,
                Error = message ?? "",
                ConsecutiveCount = consecutiveCount,
                AtUtc = DateTime.UtcNow
            };

            lock (_gate)
            {
                if (_recentHandlerErrors.Count >= MaxRetainedHandlerErrors)
                {
                    _recentHandlerErrors.Dequeue();
                }

                _recentHandlerErrors.Enqueue(entry);
            }
        }

        /// <summary>
        /// Returns a snapshot of recent Tick-time handler failures (oldest first), capped at
        /// <see cref="MaxRetainedHandlerErrors"/>. Pass <paramref name="modId"/> to filter to a single
        /// mod.
        /// </summary>
        public IReadOnlyList<LuaModHandlerError> GetRecentHandlerErrors(string modId = null)
        {
            string filter = modId == null ? null : Normalize(modId);
            List<LuaModHandlerError> result = new();
            lock (_gate)
            {
                foreach (LuaModHandlerError entry in _recentHandlerErrors)
                {
                    if (filter == null || filter.Length == 0 ||
                        string.Equals(entry.ModId, filter, StringComparison.Ordinal))
                    {
                        result.Add(entry);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Clears the recent Tick-time handler-error buffer (optionally only entries for one mod).
        /// Returns the number of entries removed.
        /// </summary>
        public int ClearRecentHandlerErrors(string modId = null)
        {
            string filter = modId == null ? null : Normalize(modId);
            lock (_gate)
            {
                if (filter == null || filter.Length == 0)
                {
                    int cleared = _recentHandlerErrors.Count;
                    _recentHandlerErrors.Clear();
                    return cleared;
                }

                int before = _recentHandlerErrors.Count;
                LuaModHandlerError[] kept = new LuaModHandlerError[before];
                int keptCount = 0;
                foreach (LuaModHandlerError entry in _recentHandlerErrors)
                {
                    if (!string.Equals(entry.ModId, filter, StringComparison.Ordinal))
                    {
                        kept[keptCount++] = entry;
                    }
                }

                _recentHandlerErrors.Clear();
                for (int i = 0; i < keptCount; i++)
                {
                    _recentHandlerErrors.Enqueue(kept[i]);
                }

                return before - keptCount;
            }
        }

        /// <summary>
        /// Appends a <c>report()</c>/<c>print()</c> emission to the bounded recent-reports buffer,
        /// dropping the oldest entry when full. Called regardless of the mod's <c>LogReports</c> flag.
        /// </summary>
        private void RecordReport(string modId, string message)
        {
            LuaModReport entry = new()
            {
                ModId = modId,
                Message = message ?? "",
                AtUtc = DateTime.UtcNow
            };

            lock (_gate)
            {
                if (_recentReports.Count >= MaxRetainedReports)
                {
                    _recentReports.Dequeue();
                }

                _recentReports.Enqueue(entry);
            }
        }

        /// <summary>
        /// Returns a snapshot of recent <c>report()</c>/<c>print()</c> emissions (oldest first), capped
        /// at <see cref="MaxRetainedReports"/>, independent of each mod's <c>LogReports</c> flag. Pass
        /// <paramref name="modId"/> to filter to a single mod.
        /// </summary>
        public IReadOnlyList<LuaModReport> GetRecentReports(string modId = null)
        {
            string filter = modId == null ? null : Normalize(modId);
            List<LuaModReport> result = new();
            lock (_gate)
            {
                foreach (LuaModReport entry in _recentReports)
                {
                    if (filter == null || filter.Length == 0 ||
                        string.Equals(entry.ModId, filter, StringComparison.Ordinal))
                    {
                        result.Add(entry);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Clears the recent reports buffer (optionally only entries for one mod). Returns the number of
        /// entries removed.
        /// </summary>
        public int ClearRecentReports(string modId = null)
        {
            string filter = modId == null ? null : Normalize(modId);
            lock (_gate)
            {
                if (filter == null || filter.Length == 0)
                {
                    int cleared = _recentReports.Count;
                    _recentReports.Clear();
                    return cleared;
                }

                int before = _recentReports.Count;
                LuaModReport[] kept = new LuaModReport[before];
                int keptCount = 0;
                foreach (LuaModReport entry in _recentReports)
                {
                    if (!string.Equals(entry.ModId, filter, StringComparison.Ordinal))
                    {
                        kept[keptCount++] = entry;
                    }
                }

                _recentReports.Clear();
                for (int i = 0; i < keptCount; i++)
                {
                    _recentReports.Enqueue(kept[i]);
                }

                return before - keptCount;
            }
        }

        private static string Normalize(string value)
        {
            return (value ?? "").Trim();
        }
    }
}
