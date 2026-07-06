using System;
using System.Collections.Generic;
using CoreAI.Logging;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using Newtonsoft.Json;

namespace CoreAI.Ai
{
    /// <summary>Snapshot of a loaded mod for diagnostics/UI.</summary>
    public sealed class LuaModInfo
    {
        public string Id = "";
        public LuaCapabilities Capabilities;
        public int HandlerCount;
        public int TimerCount;
        public int ErrorCount;
        public bool LogReports;
        public DateTime LoadedAtUtc;
    }

    /// <summary>
    /// A single Tick-time mod-handler failure captured for later inspection by the agent (via
    /// <see cref="LuaModRuntime.GetRecentHandlerErrors"/> and the <c>manage_mods diagnostics</c>
    /// action). Unlike a load/reload error — which propagates synchronously to whoever triggered it —
    /// these happen asynchronously on the host thread, so they are buffered here so the agent learns of
    /// them on a later turn and can repair the mod.
    /// </summary>
    public sealed class LuaModHandlerError
    {
        public string ModId = "";
        public string Error = "";

        /// <summary>The mod's consecutive-failure streak when this error fired (resets after any success).</summary>
        public int ConsecutiveCount;

        public DateTime AtUtc;
    }

    /// <summary>
    /// Persistent runtime for long-lived Lua mods (vs one-shot envelope scripts). A mod is a
    /// sandboxed script that registers hooks during load and then lives across frames:
    /// <list type="bullet">
    /// <item><c>hooks_on(event, fn)</c> — handler for named events (from the game or other mods).</item>
    /// <item><c>hooks_every(seconds, fn)</c> — repeating timer driven by <see cref="Tick"/>.</item>
    /// <item><c>events_emit(name, payload)</c> — emits an event to the game (<see cref="ModEventEmitted"/>) and other mods.</item>
    /// <item><c>store_set(key, value)</c> / <c>store_get(key)</c> — persistent per-mod k/v (when an <see cref="ILuaModStore"/> is supplied).</item>
    /// <item><c>mod_id()</c> — the mod's own id.</item>
    /// </list>
    /// The host (Unity layer) calls <see cref="Tick"/> once per frame; every handler call runs
    /// under a per-call instruction/time guard, and a mod failing
    /// <see cref="MaxErrorsBeforeUnload"/> times in a row (the counter resets on a successful
    /// call) is unloaded automatically.
    /// </summary>
    public sealed class LuaModRuntime
    {
        public const int DefaultHandlerTimeoutMs = 100;
        public const long DefaultHandlerMaxSteps = 100_000;
        public const int DefaultMaxMods = 32;
        public const int DefaultMaxHandlersPerMod = 64;
        public const int DefaultMaxTimersPerMod = 16;
        public const int DefaultMaxQueuedEventsPerMod = 256;
        public const int DefaultMaxEventsDispatchedPerTick = 64;

        /// <summary>
        /// Upper bound on handler invocations dispatched across <em>all</em> mods in a single
        /// <see cref="Tick"/>. The per-mod cap (<see cref="DefaultMaxEventsDispatchedPerTick"/>)
        /// alone lets a tick fan out to up to <see cref="DefaultMaxMods"/> mods, i.e.
        /// <c>DefaultMaxMods * DefaultMaxEventsDispatchedPerTick</c> calls, which is a large
        /// main-thread stall. This global budget caps the whole tick; mods not reached once it is
        /// exhausted keep their queued events and are serviced on later ticks (no events are
        /// dropped). Chosen as 4x the per-mod cap: comfortably above the per-mod cap so a single
        /// busy mod is never throttled below its own budget, while still bounding a worst-case
        /// burst across many mods to a few hundred calls per frame.
        /// </summary>
        public const int DefaultMaxEventsDispatchedPerTickGlobal = 256;

        public const int MaxErrorsBeforeUnload = 8;

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
        /// colliding with a game-defined script slot of the same name.
        /// </summary>
        public const string VersionKeyPrefix = "mod:";

        /// <summary>
        /// Upper bound on the number of recent Tick-time handler errors retained for the agent to inspect
        /// via <see cref="GetRecentHandlerErrors"/> / the <c>manage_mods diagnostics</c> action. Oldest
        /// entries are dropped once the buffer is full so a perpetually broken mod cannot grow it without
        /// bound.
        /// </summary>
        public const int MaxRetainedHandlerErrors = 32;

        private sealed class TimerEntry
        {
            public double IntervalSeconds;
            public double DueIn;
            public Closure Fn;
        }

        private sealed class Mod
        {
            public string Id = "";
            public Script Script;
            public string Source = "";
            public LuaCapabilities Caps;
            public bool LogReports;
            public readonly Dictionary<string, List<Closure>> Handlers = new(StringComparer.Ordinal);
            public readonly List<TimerEntry> Timers = new();
            public readonly Queue<KeyValuePair<string, string>> Pending = new();
            public readonly Dictionary<string, DynValue> Exports = new(StringComparer.Ordinal);
            public int HandlerCount;
            public int ErrorCount;
            public DateTime LoadedAtUtc;
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, Mod> _mods = new(StringComparer.Ordinal);
        private readonly SecureLuaEnvironment _env = new();
        private readonly LuaExecutionGuard _handlerGuard;
        private readonly IGameLuaRuntimeBindings _gameBindings;
        private readonly ILuaModStore _store;
        private readonly ILuaModSourceStore _sourceStore;
        private readonly ILuaScriptVersionStore _versionStore;
        private readonly bool _autoPersistMods;
        private readonly ILog _log;
        private readonly List<Mod> _tickScratch = new();

        /// <summary>
        /// Ring buffer of the most recent Tick-time handler failures, capped at
        /// <see cref="MaxRetainedHandlerErrors"/>. Populated under <see cref="_gate"/> from the (unguarded)
        /// tick so the agent can poll them next turn via <c>manage_mods diagnostics</c>; the synchronous
        /// load/reload error path already returns its failure to the caller, so it is not buffered here.
        /// </summary>
        private readonly Queue<LuaModHandlerError> _recentHandlerErrors = new();

        /// <summary>
        /// Round-robin start index into <see cref="_tickScratch"/> for charging the global event
        /// dispatch budget. Advancing it every tick keeps the per-tick budget from always being
        /// spent on the first few mods in iteration order, so under sustained saturation every mod
        /// is reached over a bounded number of ticks instead of the tail starving forever.
        /// </summary>
        private int _dispatchRotation;

        /// <summary>
        /// Raised when a mod calls <c>events_emit(name, payload)</c>: (modId, eventName, payload).
        /// The Unity layer bridges this to MessagePipe/game systems.
        /// </summary>
        public event Action<string, string, string> ModEventEmitted;

        /// <summary>
        /// Raised after a mod source is successfully loaded or reloaded: (modId, source, caps).
        /// Hosts can use this to persist their selected autoload mod set.
        /// </summary>
        public event Action<string, string, LuaCapabilities> ModSourceLoaded;

        /// <summary>
        /// Raised after a mod is unloaded, including automatic unloads after repeated errors:
        /// (modId, source, caps).
        /// </summary>
        public event Action<string, string, LuaCapabilities> ModSourceUnloaded;

        /// <summary>
        /// Raised when a loaded mod's hook/timer throws while running under <see cref="Tick"/>:
        /// (modId, error, consecutiveErrorCount). Unlike load/reload failures (which propagate
        /// synchronously to the caller), these happen asynchronously on the host thread long after
        /// the mod was accepted. The count is the mod's current consecutive-failure streak (it resets
        /// to zero after any successful call), so a host can bridge this into an auto-repair loop and
        /// debounce on the streak length. The mod is still loaded when this fires; a handler may call
        /// <see cref="TryGetModSource"/> to capture the failing source, but must not reload/unload
        /// synchronously; schedule that work instead.
        /// </summary>
        public event Action<string, string, int> ModHandlerErrored;

        /// <summary>
        /// Raised when a loaded mod calls <c>report(message)</c> and report logging is enabled for
        /// that mod: (modId, message). Reports are muted by default so timer mods cannot flood logs.
        /// </summary>
        public event Action<string, string> ModReportEmitted;

        /// <summary>True when the Lua sandbox is available on this platform.</summary>
        public static bool IsSupported => SecureLuaEnvironment.IsSupported;

        /// <param name="gameBindings">
        /// Game API surface granted to mods (typically an aggregator constructed for the desired
        /// <see cref="LuaCapabilities"/> tier); null = mods only get the built-in mod APIs.
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
        /// <see cref="RevertMod"/>. Null falls back to <see cref="NullLuaScriptVersionStore"/> (no history —
        /// the prior behaviour).
        /// </param>
        public LuaModRuntime(
            IGameLuaRuntimeBindings gameBindings = null,
            ILuaModStore store = null,
            ILog log = null,
            int handlerTimeoutMs = DefaultHandlerTimeoutMs,
            long handlerMaxSteps = DefaultHandlerMaxSteps,
            ILuaModSourceStore sourceStore = null,
            bool autoPersistMods = true,
            ILuaScriptVersionStore versionStore = null)
        {
            _gameBindings = gameBindings;
            _store = store;
            _log = log;
            _sourceStore = sourceStore ?? NullLuaModSourceStore.Instance;
            _versionStore = versionStore ?? new NullLuaScriptVersionStore();
            _autoPersistMods = autoPersistMods;
            _handlerGuard = new LuaExecutionGuard(handlerTimeoutMs, handlerMaxSteps);
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
                        LoadedAtUtc = mod.LoadedAtUtc
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Returns the Lua source of a loaded mod (the exact chunk passed to
        /// <see cref="LoadMod"/>/<see cref="ReloadMod"/>), so agents and tooling can inspect and
        /// rewrite their own mods. False when no mod with this id is loaded.
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
        /// Loads a mod: creates a sandboxed script with the game bindings plus mod APIs and runs
        /// the chunk (which registers its hooks). Throws on invalid input, duplicate id, mod-count
        /// limit, or script error — nothing is left registered when the load fails.
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

            _log?.Info($"[LuaModRuntime] Mod '{modId}' loaded (caps={capabilities}).");

            // Record the revision before persisting so PersistMod can stamp the manifest Version from the
            // revision count. The version store seeds the original on the first record and dedups identical
            // source, so a rehydrate (which replays the stored current source) does not create a spurious
            // entry. Recording is independent of persistToStore: a masked rehydrate/import still wants its
            // history seeded.
            RecordRevision(modId, luaCode);
            if (persistToStore)
            {
                PersistMod(modId, luaCode, capabilities);
            }

            ModSourceLoaded?.Invoke(modId, luaCode, capabilities);
        }

        /// <summary>
        /// Creates the sandboxed script with capability-scoped game bindings plus mod APIs and
        /// runs the chunk (hook registration happens there). Errors propagate to the caller and
        /// the mod is never added, so a failed build leaves no handlers behind.
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

            LuaApiRegistry registry = new();
            RegisterGameBindings(registry, capabilities);
            RegisterModApis(registry, mod);

            Script script = _env.CreateScript(registry);
            // print() inside a mod behaves like report(): same event pipeline, same LogReports mute.
            script.Options.DebugPrint = message =>
            {
                if (mod.LogReports)
                {
                    ModReportEmitted?.Invoke(mod.Id, message ?? "");
                }
            };
            mod.Script = script;

            // The game bindings (and their world transaction state) are shared with the envelope
            // and tool executors. A load/reload chunk that opens a world transaction and then errors
            // would otherwise leave that shared transaction open, silently buffering later scripts'
            // world commands. Reset before running and abort in the finally so a leaked transaction
            // cannot bleed out of mod loading.
            (_gameBindings as ILuaTransactionScope)?.ResetTransactions();
            try
            {
                _env.RunChunk(script, luaCode);
            }
            finally
            {
                (_gameBindings as ILuaTransactionScope)?.ResetTransactions();
            }

            return mod;
        }

        private void RegisterGameBindings(LuaApiRegistry registry, LuaCapabilities capabilities)
        {
            if (_gameBindings == null || capabilities == LuaCapabilities.None)
            {
                return;
            }

            if (_gameBindings is ICapabilityScopedLuaBindings scoped)
            {
                scoped.RegisterGameplayApis(registry, capabilities);
                return;
            }

            if (capabilities == LuaCapabilities.All)
            {
                _gameBindings.RegisterGameplayApis(registry);
                return;
            }

            // Fail closed: a non-scoped binding set cannot be trimmed to the requested tier, so a
            // restricted mod gets no game APIs at all instead of silently getting everything.
            _log?.Warn(
                $"[LuaModRuntime] Game bindings ({_gameBindings.GetType().Name}) do not implement " +
                $"ICapabilityScopedLuaBindings; mod requested '{capabilities}' - game APIs withheld.");
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

            _log?.Info($"[LuaModRuntime] Mod '{modId}' unloaded.");

            // Keep the persisted package but mark it dormant so it does not auto-reload next start; the
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
                    _log?.Error($"[LuaModRuntime] Source store SetActive('{modId}', false) failed: {ex}");
                }
            }

            ModSourceUnloaded?.Invoke(modId, source, caps);
            return true;
        }

        /// <summary>
        /// Replaces a loaded mod with new code, keeping its capability tier. The new chunk is
        /// built and run first; if it fails, the old mod stays loaded and untouched.
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

            lock (_gate)
            {
                _mods[modId] = replacement;
            }

            _log?.Info($"[LuaModRuntime] Mod '{modId}' reloaded (caps={caps}).");
            RecordRevision(modId, luaCode);
            PersistMod(modId, luaCode, caps);
            ModSourceLoaded?.Invoke(modId, luaCode, caps);
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
                _log?.Error($"[LuaModRuntime] Version store record for '{modId}' failed: {ex}");
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
                _log?.Error($"[LuaModRuntime] Version store snapshot for '{modId}' failed: {ex}");
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
                _log?.Error($"[LuaModRuntime] Version store snapshot for '{modId}' failed: {ex}");
                return false;
            }

            if (revisionIndex >= snapshot.History.Count)
            {
                return false;
            }

            string source = snapshot.History[revisionIndex].Source ?? "";
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            // Reload first (it can throw on a bad revision, leaving the live mod untouched), then truncate the
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
                    _log?.Error($"[LuaModRuntime] Version store revert for '{modId}' failed: {ex}");
                    return false;
                }
            }

            restoredSource = source;
            return true;
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
        /// Advances timers and dispatches queued events. Call once per frame from the host
        /// (main thread); every handler call is individually instruction/time guarded.
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
                    _tickScratch.Add(mod);
                }
            }

            // Timers always run (they are bounded to one fire per timer per tick); they are not
            // charged against the global event budget, so they run for every mod in iteration
            // order regardless of how the event budget is shared.
            for (int i = 0; i < _tickScratch.Count; i++)
            {
                TickTimers(_tickScratch[i], deltaSeconds);
            }

            // Running count of handler invocations dispatched across all mods this tick. Only event
            // dispatch is charged against the global budget so a heavy event-fan-out tick cannot
            // stall the main thread. The start index rotates round-robin every tick: charging the
            // budget always from index 0 would mean only the first DefaultMaxEventsDispatchedPerTickGlobal
            // / DefaultMaxEventsDispatchedPerTick mods are serviced under sustained saturation while
            // the tail starves (and EnqueueLocked drops their oldest events). Rotating the start
            // shares the global budget fairly across all mods over successive ticks. Once the budget
            // is exhausted, the remaining mods keep their queued events untouched for later ticks.
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
                        // Defence in depth: a single mod's dispatch failure must never abort the whole
                        // per-frame tick for the other mods. (Handler bodies are already guarded in
                        // InvokeGuarded; this catches anything escaping the dispatch bookkeeping itself.)
                        mod.ErrorCount++;
                        _log?.Error($"[LuaModRuntime] Mod '{mod.Id}' event dispatch failed: {ex}");
                    }
                }

                if (mod.ErrorCount >= MaxErrorsBeforeUnload)
                {
                    UnloadMod(mod.Id);
                    _log?.Warn(
                        $"[LuaModRuntime] Mod '{mod.Id}' unloaded after {mod.ErrorCount} handler errors.");
                }
            }
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

                // One invocation per tick maximum — a long hitch must not burst-fire a timer.
                timer.DueIn = timer.IntervalSeconds;
                InvokeGuarded(mod, timer.Fn);
            }
        }

        /// <summary>
        /// Dispatches this mod's queued events, honouring both the per-mod cap
        /// (<see cref="DefaultMaxEventsDispatchedPerTick"/>) and the shared global budget
        /// (<see cref="DefaultMaxEventsDispatchedPerTickGlobal"/>). <paramref name="alreadyDispatchedThisTick"/>
        /// is the number of handler invocations other mods already spent this tick, so the
        /// effective limit is the smaller of the per-mod cap and the remaining global budget.
        /// Returns the number of invocations this mod dispatched; surplus events stay queued and
        /// are carried over to the next tick.
        /// </summary>
        private int DispatchPendingEvents(Mod mod, int alreadyDispatchedThisTick)
        {
            int globalRemaining = DefaultMaxEventsDispatchedPerTickGlobal - alreadyDispatchedThisTick;
            int limit = Math.Min(DefaultMaxEventsDispatchedPerTick, globalRemaining);
            int dispatched = 0;
            while (dispatched < limit)
            {
                KeyValuePair<string, string> evt;
                Closure[] handlerSnapshot;
                lock (_gate)
                {
                    if (mod.Pending.Count == 0)
                    {
                        return dispatched;
                    }

                    evt = mod.Pending.Peek();

                    // Snapshot the handler list under the gate. A dispatched handler may call hooks_on()
                    // for the same event, which mutates mod.Handlers; enumerating the live list would then
                    // throw "Collection was modified" out of the (unguarded) tick. The copy isolates this
                    // dispatch from registrations made while it runs.
                    handlerSnapshot = mod.Handlers.TryGetValue(evt.Key, out List<Closure> handlers)
                        ? handlers.ToArray()
                        : Array.Empty<Closure>();

                    // Honour the no-drop contract: only dequeue when the remaining budget can run every
                    // handler of this event. Otherwise leave it queued for the next tick instead of
                    // dispatching it partially and silently losing the tail handlers. Exception: an event
                    // whose own handler count exceeds the whole budget would starve forever, so when
                    // nothing has run yet this mod we dispatch it in full (bounded by the per-mod handler
                    // cap) to guarantee progress.
                    if (handlerSnapshot.Length > limit - dispatched && dispatched > 0)
                    {
                        return dispatched;
                    }

                    mod.Pending.Dequeue();
                }

                foreach (Closure fn in handlerSnapshot)
                {
                    InvokeGuarded(mod, fn, evt.Key, evt.Value);
                    dispatched++;
                }
            }

            return dispatched;
        }

        private void InvokeGuarded(Mod mod, Closure fn, params object[] args)
        {
            try
            {
                Script owner = fn.OwnerScript;
                DynValue[] dynArgs = new DynValue[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    dynArgs[i] = DynValue.FromObject(owner, args[i]);
                }

                _handlerGuard.Execute(owner, DynValue.FromObject(owner, fn), dynArgs);

                // "MaxErrorsBeforeUnload failures in a row": a successful call forgives past
                // errors, so rare sporadic failures over a long lifetime do not unload the mod.
                mod.ErrorCount = 0;
            }
            catch (Exception ex)
            {
                mod.ErrorCount++;
                _log?.Error($"[LuaModRuntime] Mod '{mod.Id}' handler failed ({mod.ErrorCount}): {ex}");

                string message = (ex is InterpreterException ie ? ie.Message : ex.Message ?? "")
                    .Replace("\r", " ").Replace("\n", " ").Trim();

                // Buffer the failure so the agent can poll it next turn via `manage_mods diagnostics`,
                // independent of any host-side ModHandlerErrored subscriber. (A load/reload error already
                // returns synchronously to whoever triggered it; only these async Tick-time failures need
                // a feedback path back to the model.)
                RecordHandlerError(mod.Id, message, mod.ErrorCount);

                // Surface the runtime failure so hosts can drive auto-repair. Fired outside the gate
                // (InvokeGuarded never holds it); a throwing subscriber must not derail the tick.
                if (ModHandlerErrored != null)
                {
                    try
                    {
                        ModHandlerErrored.Invoke(mod.Id, message, mod.ErrorCount);
                    }
                    catch (Exception subscriberEx)
                    {
                        _log?.Error($"[LuaModRuntime] ModHandlerErrored subscriber threw: {subscriberEx}");
                    }
                }
            }
            finally
            {
                // The game bindings (and their world-transaction state) are a shared singleton
                // also used by the envelope/tool executors. A guarded handler/timer that calls
                // coreai_world_begin() and then errors (the catch above swallows the exception)
                // would otherwise leave the transaction open, silently buffering every later
                // handler's, timer's, and tick's world commands instead of publishing them.
                // Resetting per guarded call guarantees no transaction opened inside one
                // invocation can leak into the next handler, the next timer, or a later tick.
                (_gameBindings as ILuaTransactionScope)?.ResetTransactions();
            }
        }

        private void RegisterModApis(LuaApiRegistry registry, Mod mod)
        {
            registry.Register("mod_id", new Func<string>(() => mod.Id));

            registry.Register("hooks_on", new Func<string, Closure, bool>((evt, fn) =>
            {
                string name = Normalize(evt);
                if (name.Length == 0 || fn == null)
                {
                    throw new ArgumentException("hooks_on: event name and function are required.");
                }

                // LLM-written mods routinely register hooks_on("tick"/"update"/"frame", fn)
                // expecting a per-frame callback, but hooks_on only receives NAMED events and
                // nothing emits those - the handler would sit dead forever (observed live: a
                // day/night sun rotator that never rotated). Route the intuitive spelling to the
                // timer machinery at the shortest allowed interval instead.
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
                    return true;
                }

                if (mod.HandlerCount >= DefaultMaxHandlersPerMod)
                {
                    throw new InvalidOperationException(
                        $"hooks_on: handler limit reached ({DefaultMaxHandlersPerMod}).");
                }

                if (!mod.Handlers.TryGetValue(name, out List<Closure> list))
                {
                    list = new List<Closure>();
                    mod.Handlers[name] = list;
                }

                list.Add(fn);
                mod.HandlerCount++;
                return true;
            }));

            registry.Register("hooks_every", new Func<double, Closure, bool>((seconds, fn) =>
            {
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
                return true;
            }));

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

            registry.Register("mods_export", new Action<string, DynValue>((name, value) =>
            {
                string exportName = Normalize(name);
                if (exportName.Length == 0)
                {
                    throw new ArgumentException("mods_export: name is required.");
                }

                lock (_gate)
                {
                    if (!mod.Exports.ContainsKey(exportName) && mod.Exports.Count >= DefaultMaxExportsPerMod)
                    {
                        throw new InvalidOperationException(
                            $"mods_export: export limit reached ({DefaultMaxExportsPerMod}).");
                    }

                    mod.Exports[exportName] = value ?? DynValue.Nil;
                }
            }));

            registry.Register("mods_get", new Func<string, string, DynValue>((targetId, name) =>
            {
                DynValue export = FindExport(targetId, name, out Mod _);
                if (export.Type == DataType.Function || export.Type == DataType.ClrFunction)
                {
                    throw new ArgumentException(
                        $"mods_get: '{Normalize(name)}' of mod '{Normalize(targetId)}' is a function - use mods_call.");
                }

                // Marshal by value: MoonSharp values are per-script, and sharing a live table
                // between sandboxes would let one mod mutate another's state behind its back.
                return FromPortable(mod.Script, ToPortable(export, CrossModTableDepth));
            }));

            // Varargs need raw CallbackArguments: a typed DynValue[] parameter would make
            // MoonSharp try to squeeze the third Lua argument into the array itself.
            registry.RegisterCallback("mods_call", (context, callbackArgs) =>
            {
                string targetId = callbackArgs.Count > 0 ? callbackArgs[0].CastToString() : null;
                string name = callbackArgs.Count > 1 ? callbackArgs[1].CastToString() : null;
                DynValue export = FindExport(targetId, name, out Mod target);
                if (export.Type != DataType.Function)
                {
                    throw new ArgumentException(
                        $"mods_call: '{Normalize(name)}' of mod '{Normalize(targetId)}' is not a function - use mods_get.");
                }

                if (_crossCallDepth >= MaxCrossCallDepth)
                {
                    throw new InvalidOperationException(
                        $"mods_call: cross-mod call depth limit reached ({MaxCrossCallDepth}) - break the cycle.");
                }

                DynValue[] marshalled = new DynValue[Math.Max(0, callbackArgs.Count - 2)];
                for (int i = 0; i < marshalled.Length; i++)
                {
                    marshalled[i] = FromPortable(target.Script, ToPortable(callbackArgs[i + 2], CrossModTableDepth));
                }

                _crossCallDepth++;
                try
                {
                    DynValue result = _handlerGuard.Execute(target.Script, export, marshalled);
                    return FromPortable(mod.Script, ToPortable(result, CrossModTableDepth));
                }
                finally
                {
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
                if (!mod.LogReports)
                {
                    return;
                }

                ModReportEmitted?.Invoke(mod.Id, message ?? "");
            }));
        }

        // Reentrancy depth of mods_call on the current thread (ticks run on the main thread; a
        // second thread would only ever see its own chain).
        [ThreadStatic] private static int _crossCallDepth;

        /// <summary>Resolves a mod's export or throws a descriptive error naming what is missing.</summary>
        private DynValue FindExport(string targetId, string name, out Mod target)
        {
            string modId = Normalize(targetId);
            string exportName = Normalize(name);
            lock (_gate)
            {
                if (!_mods.TryGetValue(modId, out target))
                {
                    throw new ArgumentException($"mod '{modId}' is not loaded.");
                }

                if (!target.Exports.TryGetValue(exportName, out DynValue export))
                {
                    throw new ArgumentException(
                        $"mod '{modId}' has no export '{exportName}' (mods_list_exports lists available names).");
                }

                return export;
            }
        }

        /// <summary>
        /// Converts a Lua value to a script-independent representation: nil/boolean/number/string
        /// plus tables up to <paramref name="depth"/> levels. MoonSharp values belong to one Script;
        /// cross-mod reads/calls marshal BY VALUE so no mod can mutate another's live state.
        /// </summary>
        private static object ToPortable(DynValue value, int depth)
        {
            if (value == null || value.IsNil() || value.Type == DataType.Void)
            {
                return null;
            }

            switch (value.Type)
            {
                case DataType.Boolean:
                    return value.Boolean;
                case DataType.Number:
                    return value.Number;
                case DataType.String:
                    return value.String;
                case DataType.Table:
                    if (depth <= 0)
                    {
                        throw new ArgumentException(
                            $"cross-mod tables may nest at most {CrossModTableDepth} levels.");
                    }

                    List<KeyValuePair<object, object>> pairs = new();
                    foreach (TablePair pair in value.Table.Pairs)
                    {
                        pairs.Add(new KeyValuePair<object, object>(
                            ToPortable(pair.Key, depth - 1),
                            ToPortable(pair.Value, depth - 1)));
                    }

                    return pairs;
                default:
                    throw new ArgumentException(
                        $"cross-mod values must be nil/boolean/number/string/table (got {value.Type}).");
            }
        }

        /// <summary>Rebuilds a <see cref="ToPortable"/> value inside the receiving mod's script.</summary>
        private static DynValue FromPortable(Script script, object value)
        {
            switch (value)
            {
                case null:
                    return DynValue.Nil;
                case bool b:
                    return DynValue.NewBoolean(b);
                case double d:
                    return DynValue.NewNumber(d);
                case string s:
                    return DynValue.NewString(s);
                case List<KeyValuePair<object, object>> pairs:
                {
                    Table table = new(script);
                    foreach (KeyValuePair<object, object> pair in pairs)
                    {
                        table.Set(FromPortable(script, pair.Key), FromPortable(script, pair.Value));
                    }

                    return DynValue.NewTable(table);
                }
                default:
                    return DynValue.Nil;
            }
        }

        private void EmitFromMod(Mod sender, string evt, string payload)
        {
            // Deliver to every other mod's queue (no self-delivery: trivial infinite loops).
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

            ModEventEmitted?.Invoke(sender.Id, evt, payload);
        }

        private void EnqueueLocked(Mod mod, string evt, string payload)
        {
            if (mod.Pending.Count >= DefaultMaxQueuedEventsPerMod)
            {
                // Drop oldest: a stalled mod must not grow its queue without bound.
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
                _log?.Error($"[LuaModRuntime] Source store Delete('{modId}') failed: {ex}");
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
                _log?.Error($"[LuaModRuntime] Source store List() failed during rehydrate: {ex}");
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
                        _log?.Warn($"[LuaModRuntime] Rehydrate skipped '{modId}': no source in store.");
                        continue;
                    }

                    string capsText = stored != null ? stored.Capabilities : manifest.Capabilities;
                    LuaCapabilities effectiveCaps = ApplyHostGrant(ParseCaps(capsText), hostGrant, allowFull);

                    // Load with the masked runtime tier but do NOT re-persist: the stored manifest already
                    // holds the mod's declared capabilities. Overwriting it with the masked tier would
                    // permanently strip Full from the store, so a later allowFull rehydrate could not
                    // restore it.
                    LoadMod(modId, source, effectiveCaps, false);
                    loaded++;
                }
                catch (Exception ex)
                {
                    _log?.Error($"[LuaModRuntime] Rehydrate of mod '{modId}' failed: {ex}");
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
                    _log?.Error($"[LuaModRuntime] Source store TryLoad('{modId}') failed during export: {ex}");
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
                _log?.Error($"[LuaModRuntime] Export of mod '{modId}' failed: {ex}");
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
                _log?.Error($"[LuaModRuntime] ImportMod failed to parse bundle: {ex}");
                return false;
            }

            if (bundle == null || string.IsNullOrWhiteSpace(bundle.Source))
            {
                _log?.Warn("[LuaModRuntime] ImportMod rejected: missing source.");
                return false;
            }

            string modId = Normalize(bundle.Manifest != null ? bundle.Manifest.Id : "");
            if (modId.Length == 0)
            {
                _log?.Warn("[LuaModRuntime] ImportMod rejected: missing/blank mod id.");
                return false;
            }

            string capsText = bundle.Manifest != null ? bundle.Manifest.Capabilities : "";
            LuaCapabilities effectiveCaps = ApplyHostGrant(ParseCaps(capsText), hostGrant, allowFull);

            try
            {
                if (IsLoaded(modId))
                {
                    ReloadMod(modId, bundle.Source);
                }
                else
                {
                    // Run with the masked runtime tier, but persist the DECLARED capabilities from the
                    // bundle (not the masked tier) so a later allowFull rehydrate can restore the full
                    // request rather than the stripped-down version.
                    LoadMod(modId, bundle.Source, effectiveCaps, false);
                    PersistMod(modId, bundle.Source, ParseCaps(capsText));
                }

                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"[LuaModRuntime] ImportMod of '{modId}' failed: {ex}");
                return false;
            }
        }

        /// <summary>JSON shape of an export/import bundle: the manifest plus the raw Lua source.</summary>
        private sealed class LuaModBundle
        {
            [JsonProperty("manifest")] public LuaModManifest Manifest;

            [JsonProperty("source")] public string Source = "";
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
                _log?.Error($"[LuaModRuntime] Source store Save('{modId}') failed: {ex}");
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
                _log?.Error($"[LuaModRuntime] Version store version lookup for '{id}' failed: {ex}");
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

            _log?.Warn($"[LuaModRuntime] Unrecognized capability string '{text}'; defaulting to None.");
            return LuaCapabilities.None;
        }

        /// <summary>
        /// Appends a Tick-time handler failure to the bounded recent-errors buffer, dropping the oldest entry
        /// when full. Called from the (gate-free) <see cref="InvokeGuarded"/> catch, so it takes the gate only
        /// to mutate the queue.
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
        /// <see cref="MaxRetainedHandlerErrors"/>. The agent polls these via <c>manage_mods diagnostics</c> so
        /// it learns of mods whose hooks/timers throw at runtime — failures that, unlike load/reload errors, are
        /// never returned synchronously. Pass <paramref name="modId"/> to filter to a single mod.
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
        /// Clears the recent Tick-time handler-error buffer (optionally only entries for one mod), so an agent
        /// that has repaired a mod can acknowledge its failures and stop seeing them on later diagnostics polls.
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

        private static string Normalize(string value)
        {
            return (value ?? "").Trim();
        }
    }
}
