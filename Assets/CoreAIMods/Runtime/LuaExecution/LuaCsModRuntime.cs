using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Logging;
using CoreAI.Sandbox.LuaCs;
using Lua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Snapshot of a loaded Lua-CSharp mod for diagnostics/UI. Mirrors
    /// <see cref="CoreAI.Ai.LuaModInfo"/> (the MoonSharp runtime's snapshot) field-for-field so
    /// hosts and tooling can render either VM's mods through the same shape.
    /// </summary>
    public sealed class LuaCsModInfo
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
    /// <see cref="LuaCsModRuntime.GetRecentHandlerErrors"/>). Mirrors
    /// <see cref="CoreAI.Ai.LuaModHandlerError"/>. Unlike a load/reload error — which propagates
    /// synchronously to whoever triggered it — these happen asynchronously on the host thread, so
    /// they are buffered here so the agent learns of them on a later turn and can repair the mod.
    /// </summary>
    public sealed class LuaCsModHandlerError
    {
        public string ModId = "";
        public string Error = "";

        /// <summary>The mod's consecutive-failure streak when this error fired (resets after any success).</summary>
        public int ConsecutiveCount;

        public DateTime AtUtc;
    }

    /// <summary>
    /// Lua-CSharp (nuskey8/Lua-CSharp) persistent runtime for long-lived mods. This is the ADDITIVE
    /// counterpart of the MoonSharp <see cref="CoreAI.Ai.LuaModRuntime"/>, built as part of the
    /// MoonSharp -> Lua-CSharp migration: both VMs coexist and the tick driver can later swap
    /// <c>LuaModRuntime</c> -> <c>LuaCsModRuntime</c> by type because the public lifecycle/tick/
    /// diagnostics surface below mirrors the MoonSharp runtime.
    ///
    /// A mod is a sandboxed Lua-CSharp <see cref="LuaState"/> that registers hooks during load and
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
    /// per-call instruction/time guard (<see cref="LuaCsExecutionGuard"/>), and a mod failing
    /// <see cref="MaxErrorsBeforeUnload"/> times in a row (the counter resets on a successful call)
    /// is unloaded automatically.
    ///
    /// SCOPE NOTE (migration pass 1): the heavy world/unity gameplay bindings that the MoonSharp
    /// runtime injects via <c>IGameLuaRuntimeBindings.RegisterGameplayApis(LuaApiRegistry)</c> are
    /// NOT ported here. This runtime accepts an injection callback so ported gameplay APIs can be
    /// wired later; see <see cref="RegisterGameplayBindings"/> for the open seam. Source/version
    /// stores, import/export and rehydrate (present on the MoonSharp runtime) are likewise deferred
    /// to a later pass and intentionally omitted to keep this VM addition bounded and compiling.
    /// </summary>
    public sealed class LuaCsModRuntime
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
        /// <see cref="Tick"/>. Chosen as 4x the per-mod cap: comfortably above the per-mod cap so a
        /// single busy mod is never throttled below its own budget, while still bounding a worst-case
        /// burst across many mods to a few hundred calls per frame. Mods not reached once it is
        /// exhausted keep their queued events for later ticks (no events are dropped).
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
        /// Upper bound on the number of recent Tick-time handler errors retained for the agent to
        /// inspect via <see cref="GetRecentHandlerErrors"/>. Oldest entries are dropped once the buffer
        /// is full so a perpetually broken mod cannot grow it without bound.
        /// </summary>
        public const int MaxRetainedHandlerErrors = 32;

        private sealed class TimerEntry
        {
            public double IntervalSeconds;
            public double DueIn;
            public LuaFunction Fn;
        }

        private sealed class Mod
        {
            public string Id = "";
            public LuaState State;
            public string Source = "";
            public LuaCapabilities Caps;
            public bool LogReports;
            public readonly Dictionary<string, List<LuaFunction>> Handlers = new(StringComparer.Ordinal);
            public readonly List<TimerEntry> Timers = new();
            public readonly Queue<KeyValuePair<string, string>> Pending = new();
            public readonly Dictionary<string, LuaValue> Exports = new(StringComparer.Ordinal);
            public int HandlerCount;
            public int ErrorCount;
            public DateTime LoadedAtUtc;
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, Mod> _mods = new(StringComparer.Ordinal);
        private readonly LuaCsSecureEnvironment _env = new();
        private readonly LuaCsExecutionGuard _handlerGuard;
        private readonly Action<LuaCsApiRegistry, LuaCapabilities> _gameplayBindings;
        private readonly ILuaModStore _store;
        private readonly ILog _log;
        private readonly List<Mod> _tickScratch = new();

        private readonly Queue<LuaCsModHandlerError> _recentHandlerErrors = new();

        /// <summary>
        /// Round-robin start index for charging the global event dispatch budget so, under sustained
        /// saturation, every mod is reached over a bounded number of ticks instead of the tail
        /// starving forever.
        /// </summary>
        private int _dispatchRotation;

        // Reentrancy depth of mods_call on the current thread (ticks run on the main thread; a
        // second thread would only ever see its own chain).
        [ThreadStatic] private static int _crossCallDepth;

        /// <summary>
        /// Raised when a mod calls <c>events_emit(name, payload)</c>: (modId, eventName, payload).
        /// The Unity layer bridges this to MessagePipe/game systems.
        /// </summary>
        public event Action<string, string, string> ModEventEmitted;

        /// <summary>Raised after a mod source is successfully loaded or reloaded: (modId, source, caps).</summary>
        public event Action<string, string, LuaCapabilities> ModSourceLoaded;

        /// <summary>Raised after a mod is unloaded, including automatic unloads after repeated errors: (modId, source, caps).</summary>
        public event Action<string, string, LuaCapabilities> ModSourceUnloaded;

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

        /// <param name="gameplayBindings">
        /// Optional seam for registering ported world/unity gameplay APIs on each mod's
        /// <see cref="LuaCsApiRegistry"/>, scoped to the mod's granted <see cref="LuaCapabilities"/>.
        /// Null = mods only get the built-in mod-core APIs. See <see cref="RegisterGameplayBindings"/>.
        /// </param>
        /// <param name="store">Optional persistent per-mod k/v store backing <c>store_set/get</c>.</param>
        /// <param name="log">Optional logger.</param>
        /// <param name="handlerTimeoutMs">Wall-clock budget per handler/timer call.</param>
        /// <param name="handlerMaxSteps">Instruction budget per handler/timer call.</param>
        public LuaCsModRuntime(
            Action<LuaCsApiRegistry, LuaCapabilities> gameplayBindings = null,
            ILuaModStore store = null,
            ILog log = null,
            int handlerTimeoutMs = DefaultHandlerTimeoutMs,
            long handlerMaxSteps = DefaultHandlerMaxSteps)
        {
            _gameplayBindings = gameplayBindings;
            _store = store;
            _log = log;
            _handlerGuard = new LuaCsExecutionGuard(handlerTimeoutMs, handlerMaxSteps);
        }

        /// <summary>Snapshot of all loaded mods.</summary>
        public IReadOnlyList<LuaCsModInfo> ListMods()
        {
            List<LuaCsModInfo> result = new();
            lock (_gate)
            {
                foreach (Mod mod in _mods.Values)
                {
                    result.Add(new LuaCsModInfo
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
        public void LoadMod(
            string id,
            string luaCode,
            LuaCapabilities capabilities = LuaCapabilities.All)
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
            ModSourceLoaded?.Invoke(modId, luaCode, capabilities);
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

            LuaCsApiRegistry registry = new();
            RegisterGameplayBindings(registry, capabilities);
            RegisterModApis(registry, mod);

            // Create the state BEFORE running the chunk; the mod-core callbacks capture `mod` and read
            // mod.State (set here) only when they later run, so self-referential cross-mod calls made
            // during load resolve correctly.
            mod.State = _env.Create(registry);

            // TODO(migration): reset the ported world/unity transaction scope around the load chunk
            // (the MoonSharp runtime calls ILuaTransactionScope.ResetTransactions() before/after the
            // chunk so a transaction left open by a failing load cannot bleed into later scripts).
            // There is no ported transaction surface yet, so this is a no-op seam for now.
            _env.RunChunk(mod.State, luaCode);

            return mod;
        }

        /// <summary>
        /// SEAM — <c>// TODO(migration): connect ported world/unity gameplay bindings here.</c>
        /// The MoonSharp runtime registers ~36 capability-scoped world/unity APIs via
        /// <c>IGameLuaRuntimeBindings.RegisterGameplayApis(LuaApiRegistry)</c> (optionally scoped by
        /// <c>ICapabilityScopedLuaBindings</c>). Those heavy bindings are NOT ported to Lua-CSharp in
        /// this pass. Until a ported binding provider exists, a host may inject an
        /// <see cref="Action{LuaCsApiRegistry, LuaCapabilities}"/> that registers gameplay APIs on the
        /// per-mod <see cref="LuaCsApiRegistry"/>, scoped to <paramref name="capabilities"/>. The
        /// callback is responsible for its own fail-closed capability trimming.
        /// </summary>
        private void RegisterGameplayBindings(LuaCsApiRegistry registry, LuaCapabilities capabilities)
        {
            if (_gameplayBindings == null || capabilities == LuaCapabilities.None)
            {
                return;
            }

            // TODO(migration): connect ported world/unity gameplay bindings here.
            _gameplayBindings(registry, capabilities);
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

            _log?.Info($"[LuaCsModRuntime] Mod '{modId}' unloaded.");
            ModSourceUnloaded?.Invoke(modId, source, caps);
            return true;
        }

        /// <summary>
        /// Replaces a loaded mod with new code, keeping its capability tier. The new chunk is built and
        /// run first; if it fails, the old mod stays loaded and untouched.
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

            _log?.Info($"[LuaCsModRuntime] Mod '{modId}' reloaded (caps={caps}).");
            ModSourceLoaded?.Invoke(modId, luaCode, caps);
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
                    _tickScratch.Add(mod);
                }
            }

            // Timers always run (bounded to one fire per timer per tick); they are not charged against
            // the global event budget, so they run for every mod in iteration order.
            for (int i = 0; i < _tickScratch.Count; i++)
            {
                TickTimers(_tickScratch[i], deltaSeconds);
            }

            // Only event dispatch is charged against the global budget. The start index rotates
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
                        // Defence in depth: a single mod's dispatch failure must never abort the whole
                        // per-frame tick for the other mods.
                        mod.ErrorCount++;
                        _log?.Error($"[LuaCsModRuntime] Mod '{mod.Id}' event dispatch failed: {ex}");
                    }
                }

                if (mod.ErrorCount >= MaxErrorsBeforeUnload)
                {
                    UnloadMod(mod.Id);
                    _log?.Warn(
                        $"[LuaCsModRuntime] Mod '{mod.Id}' unloaded after {mod.ErrorCount} handler errors.");
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
                LuaFunction[] handlerSnapshot;
                lock (_gate)
                {
                    if (mod.Pending.Count == 0)
                    {
                        return dispatched;
                    }

                    evt = mod.Pending.Peek();

                    // Snapshot the handler list under the gate: a dispatched handler may call hooks_on()
                    // for the same event, mutating mod.Handlers; enumerating the live list would then
                    // throw out of the (unguarded) tick.
                    handlerSnapshot = mod.Handlers.TryGetValue(evt.Key, out List<LuaFunction> handlers)
                        ? handlers.ToArray()
                        : Array.Empty<LuaFunction>();

                    // No-drop contract: only dequeue when the remaining budget can run every handler of
                    // this event. Exception: an event whose own handler count exceeds the whole budget
                    // would starve forever, so when nothing has run yet for this mod we dispatch it in
                    // full (bounded by the per-mod handler cap) to guarantee progress.
                    if (handlerSnapshot.Length > limit - dispatched && dispatched > 0)
                    {
                        return dispatched;
                    }

                    mod.Pending.Dequeue();
                }

                foreach (LuaFunction fn in handlerSnapshot)
                {
                    InvokeGuarded(mod, fn, evt.Key, evt.Value);
                    dispatched++;
                }
            }

            return dispatched;
        }

        private void InvokeGuarded(Mod mod, LuaFunction fn, params object[] args)
        {
            try
            {
                LuaValue[] luaArgs = new LuaValue[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    luaArgs[i] = HostToLua(args[i]);
                }

                _handlerGuard.Execute(mod.State, fn, CancellationToken.None, luaArgs);

                // "MaxErrorsBeforeUnload failures in a row": a successful call forgives past errors, so
                // rare sporadic failures over a long lifetime do not unload the mod.
                mod.ErrorCount = 0;
            }
            catch (Exception ex)
            {
                mod.ErrorCount++;
                _log?.Error($"[LuaCsModRuntime] Mod '{mod.Id}' handler failed ({mod.ErrorCount}): {ex}");

                string message = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (message.Length == 0)
                {
                    message = ex.GetType().Name;
                }

                // Buffer the failure so the agent can poll it next turn, independent of any host-side
                // ModHandlerErrored subscriber.
                RecordHandlerError(mod.Id, message, mod.ErrorCount);

                // Surface the runtime failure so hosts can drive auto-repair. Fired outside the gate; a
                // throwing subscriber must not derail the tick.
                if (ModHandlerErrored != null)
                {
                    try
                    {
                        ModHandlerErrored.Invoke(mod.Id, message, mod.ErrorCount);
                    }
                    catch (Exception subscriberEx)
                    {
                        _log?.Error($"[LuaCsModRuntime] ModHandlerErrored subscriber threw: {subscriberEx}");
                    }
                }
            }
            finally
            {
                // TODO(migration): reset the ported world/unity transaction scope here (the MoonSharp
                // runtime resets ILuaTransactionScope per guarded call so a transaction opened inside one
                // invocation cannot leak into the next handler/timer/tick). No-op until gameplay bindings
                // are ported.
            }
        }

        private void RegisterModApis(LuaCsApiRegistry registry, Mod mod)
        {
            registry.Register("mod_id", new Func<string>(() => mod.Id));

            registry.Register("hooks_on", new Func<string, LuaValue, bool>((evt, fnValue) =>
            {
                string name = Normalize(evt);
                LuaFunction fn = ReadFunction(fnValue);
                if (name.Length == 0 || fn == null)
                {
                    throw new ArgumentException("hooks_on: event name and function are required.");
                }

                // LLM-written mods routinely register hooks_on("tick"/"update"/"frame", fn) expecting a
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
                    return true;
                }

                if (mod.HandlerCount >= DefaultMaxHandlersPerMod)
                {
                    throw new InvalidOperationException(
                        $"hooks_on: handler limit reached ({DefaultMaxHandlersPerMod}).");
                }

                if (!mod.Handlers.TryGetValue(name, out List<LuaFunction> list))
                {
                    list = new List<LuaFunction>();
                    mod.Handlers[name] = list;
                }

                list.Add(fn);
                mod.HandlerCount++;
                return true;
            }));

            registry.Register("hooks_every", new Func<double, LuaValue, bool>((seconds, fnValue) =>
            {
                LuaFunction fn = ReadFunction(fnValue);
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

            registry.Register("mods_export", new Action<string, LuaValue>((name, value) =>
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

                    mod.Exports[exportName] = value;
                }
            }));

            registry.Register("mods_get", new Func<string, string, LuaValue>((targetId, name) =>
            {
                LuaValue export = FindExport(targetId, name, out Mod _);
                if (export.Type == LuaValueType.Function)
                {
                    throw new ArgumentException(
                        $"mods_get: '{Normalize(name)}' of mod '{Normalize(targetId)}' is a function - use mods_call.");
                }

                // Marshal by value: cross-mod reads copy plain data only (no functions/closures/live
                // refs), so no mod can mutate another's state behind its back — the multiplayer-
                // determinism rule.
                return FromPortable(ToPortable(export, CrossModTableDepth));
            }));

            // Varargs need the raw execution context: a typed LuaValue[] parameter cannot express
            // "the third and subsequent Lua arguments".
            registry.RegisterCallback("mods_call", (ctx, ct) =>
            {
                string targetId = ctx.HasArgument(0) ? ctx.GetArgument(0).Read<string>() : null;
                string name = ctx.HasArgument(1) ? ctx.GetArgument(1).Read<string>() : null;
                LuaValue export = FindExport(targetId, name, out Mod target);
                if (export.Type != LuaValueType.Function)
                {
                    throw new ArgumentException(
                        $"mods_call: '{Normalize(name)}' of mod '{Normalize(targetId)}' is not a function - use mods_get.");
                }

                if (_crossCallDepth >= MaxCrossCallDepth)
                {
                    throw new InvalidOperationException(
                        $"mods_call: cross-mod call depth limit reached ({MaxCrossCallDepth}) - break the cycle.");
                }

                int extra = Math.Max(0, ctx.ArgumentCount - 2);
                LuaValue[] marshalled = new LuaValue[extra];
                for (int i = 0; i < extra; i++)
                {
                    // Copy caller args into plain data, then rebuild them for the callee's state.
                    marshalled[i] = FromPortable(ToPortable(ctx.GetArgument(i + 2), CrossModTableDepth));
                }

                LuaFunction exportFn = export.Read<LuaFunction>();
                _crossCallDepth++;
                try
                {
                    LuaValue[] results =
                        _handlerGuard.Execute(target.State, exportFn, CancellationToken.None, marshalled);
                    LuaValue first = results.Length > 0 ? results[0] : LuaValue.Nil;

                    // Marshal the result back into the caller's state by value.
                    return new System.Threading.Tasks.ValueTask<int>(
                        ctx.Return(FromPortable(ToPortable(first, CrossModTableDepth))));
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

            // print() inside a mod behaves like report(): same event pipeline, same LogReports mute.
            // Overrides the basic library's print on this mod's environment.
            registry.RegisterCallback("print", (ctx, ct) =>
            {
                if (mod.LogReports)
                {
                    string[] parts = new string[ctx.ArgumentCount];
                    for (int i = 0; i < ctx.ArgumentCount; i++)
                    {
                        parts[i] = ctx.GetArgument(i).ToString();
                    }

                    ModReportEmitted?.Invoke(mod.Id, string.Join("\t", parts));
                }

                return new System.Threading.Tasks.ValueTask<int>(ctx.Return());
            });
        }

        /// <summary>Resolves a mod's export or throws a descriptive error naming what is missing.</summary>
        private LuaValue FindExport(string targetId, string name, out Mod target)
        {
            string modId = Normalize(targetId);
            string exportName = Normalize(name);
            lock (_gate)
            {
                if (!_mods.TryGetValue(modId, out target))
                {
                    throw new ArgumentException($"mod '{modId}' is not loaded.");
                }

                if (!target.Exports.TryGetValue(exportName, out LuaValue export))
                {
                    throw new ArgumentException(
                        $"mod '{modId}' has no export '{exportName}' (mods_list_exports lists available names).");
                }

                return export;
            }
        }

        /// <summary>Reads a Lua-CSharp function value, or null when the value is not a function.</summary>
        private static LuaFunction ReadFunction(LuaValue value)
        {
            return value.Type == LuaValueType.Function ? value.Read<LuaFunction>() : null;
        }

        private static LuaValue HostToLua(object arg)
        {
            switch (arg)
            {
                case null:
                    return LuaValue.Nil;
                case string s:
                    return new LuaValue(s);
                case bool b:
                    return new LuaValue(b);
                case double d:
                    return new LuaValue(d);
                case int i:
                    return new LuaValue((double)i);
                case long l:
                    return new LuaValue((double)l);
                case float f:
                    return new LuaValue((double)f);
                default:
                    return LuaValue.FromObject(arg);
            }
        }

        /// <summary>
        /// Converts a Lua value to a state-independent representation: nil/boolean/number/string plus
        /// tables up to <paramref name="depth"/> levels. Cross-mod reads/calls marshal BY VALUE so no
        /// mod can mutate another's live state and no function/closure/live ref crosses the boundary.
        /// </summary>
        private static object ToPortable(LuaValue value, int depth)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil:
                    return null;
                case LuaValueType.Boolean:
                    return value.Read<bool>();
                case LuaValueType.Number:
                    return value.Read<double>();
                case LuaValueType.String:
                    return value.Read<string>();
                case LuaValueType.Table:
                    if (depth <= 0)
                    {
                        throw new ArgumentException(
                            $"cross-mod tables may nest at most {CrossModTableDepth} levels.");
                    }

                    LuaTable table = value.Read<LuaTable>();
                    List<KeyValuePair<object, object>> pairs = new();
                    foreach (KeyValuePair<LuaValue, LuaValue> pair in table)
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

        /// <summary>Rebuilds a <see cref="ToPortable"/> value as a fresh Lua-CSharp value (new tables).</summary>
        private static LuaValue FromPortable(object value)
        {
            switch (value)
            {
                case null:
                    return LuaValue.Nil;
                case bool b:
                    return new LuaValue(b);
                case double d:
                    return new LuaValue(d);
                case string s:
                    return new LuaValue(s);
                case List<KeyValuePair<object, object>> pairs:
                {
                    LuaTable table = new();
                    foreach (KeyValuePair<object, object> pair in pairs)
                    {
                        table[FromPortable(pair.Key)] = FromPortable(pair.Value);
                    }

                    return new LuaValue(table);
                }
                default:
                    return LuaValue.Nil;
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
        /// Appends a Tick-time handler failure to the bounded recent-errors buffer, dropping the oldest
        /// entry when full.
        /// </summary>
        private void RecordHandlerError(string modId, string message, int consecutiveCount)
        {
            LuaCsModHandlerError entry = new()
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
        public IReadOnlyList<LuaCsModHandlerError> GetRecentHandlerErrors(string modId = null)
        {
            string filter = modId == null ? null : Normalize(modId);
            List<LuaCsModHandlerError> result = new();
            lock (_gate)
            {
                foreach (LuaCsModHandlerError entry in _recentHandlerErrors)
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
                LuaCsModHandlerError[] kept = new LuaCsModHandlerError[before];
                int keptCount = 0;
                foreach (LuaCsModHandlerError entry in _recentHandlerErrors)
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
